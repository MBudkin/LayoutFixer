using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using LayoutFixer.Input;
using LayoutFixer.Services;
using LayoutFixer.Settings;
using LayoutFixer.UI;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class SettingsAndUpdatesTests
{
    // ── update check: versions and release parsing ──────────────────────────

    [Theory]
    [InlineData("v1.10.0", "1.10.0")]
    [InlineData("1.10", "1.10.0")]
    [InlineData("LayoutFixer 2.0.1-beta", "2.0.1")]
    [InlineData("release-1.9.3.7", "1.9.3")]
    public void VersionIsReadFromTagOrTitle(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void TextWithoutVersionGivesNull(string? text)
    {
        Assert.Null(UpdateChecker.ParseVersion(text));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.9.1", "1.9.0", true)]
    [InlineData("1.9.0", "1.9.0", false)]
    [InlineData("1.9", "1.9.0", false)]
    [InlineData("1.8.9", "1.9.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    public void NewerVersionComparison(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(Version.Parse(candidate), Version.Parse(current)));
    }

    [Fact]
    public void ReleaseJsonIsParsed()
    {
        const string json = """
            {
              "tag_name": "v1.11.0",
              "name": "LayoutFixer 1.11.0",
              "html_url": "https://github.com/MBudkin/LayoutFixer/releases/tag/v1.11.0",
              "body": "## Новое\n- **Проверка** обновлений",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-10-10T12:00:00Z"
            }
            """;
        UpdateInfo? info = UpdateChecker.ParseRelease(json);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 11, 0), info!.Version);
        Assert.Equal("LayoutFixer 1.11.0", info.Title);
        Assert.Equal("https://github.com/MBudkin/LayoutFixer/releases/tag/v1.11.0", info.PageUrl);
        Assert.NotNull(info.PublishedAt);
        Assert.Equal("Новое" + Environment.NewLine + "• Проверка обновлений", UpdateForm.CleanNotes(info.Notes));
    }

    [Fact]
    public void ForeignReleaseLinkFallsBackToProjectReleases()
    {
        const string json = """{ "tag_name": "v9.0.0", "html_url": "https://evil.example/download" }""";
        UpdateInfo? info = UpdateChecker.ParseRelease(json);
        Assert.Equal(ProjectLinks.ReleasesUrl, info!.PageUrl);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v9.0.0", "draft": true }""")]
    [InlineData("""{ "tag_name": "v9.0.0", "prerelease": true }""")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    public void DraftsPrereleasesAndUnversionedTagsAreIgnored(string json)
    {
        Assert.Null(UpdateChecker.ParseRelease(json));
    }

    [Fact]
    public void CurrentVersionHasThreeComponents()
    {
        Assert.True(UpdateChecker.CurrentVersion.Build >= 0);
        Assert.Equal(-1, UpdateChecker.CurrentVersion.Revision);
    }

    // ── settings ─────────────────────────────────────────────────────────────

    // Bookkeeping that belongs to this computer and is deliberately not restored.
    private static readonly HashSet<string> MachineLocalSettings = new()
    {
        nameof(AppSettings.LastUpdateCheckUtc),
        nameof(AppSettings.SkippedUpdateVersion),
        nameof(AppSettings.AutoAddToUserDictionaryLegacy)
    };

    [Fact]
    public void BackupRestoreCopiesEverySetting()
    {
        // Regression: the 1.9.0 backup import forgot exclusions, sound and
        // newer case options. Fill every property with a non-default value
        // and check that CopyFrom carries all of them.
        var source = new AppSettings
        {
            Language = "en",
            Hotkeys = new HotkeySettings { ConvertLastWord = "F12" },
            ShowNotifications = false,
            TrayIconMode = TrayIconMode.Flag,
            NotificationDurationSec = 42,
            PlaySoundOnConversion = false,
            SoundVariant = "custom",
            CustomSoundPath = @"C:\x.wav",
            SoundVolume = 33,
            StartWithWindows = true,
            ClearBufferAfterConversion = true,
            AutoMode = false,
            AlwaysConvertSingleLetters = false,
            AutoCapitalizeSentences = true,
            FixAccidentalCapsLock = false,
            FixTwoInitialCapitals = false,
            DisableInFullscreenApps = false,
            ExcludedApps = new List<string> { "game.exe" },
            InvertEachCharacterCase = true,
            AutoReplace = false,
            TabAsWordSeparator = false,
            ReplacementsRespectPunctuation = false,
            ManualLearnMode = RememberMode.Never,
            SelectionLearnMode = RememberMode.Ask,
            EnableDebugLog = true,
            CheckForUpdates = false
        };

        var target = new AppSettings();
        target.CopyFrom(source);

        foreach (PropertyInfo property in typeof(AppSettings).GetProperties())
        {
            if (MachineLocalSettings.Contains(property.Name))
            {
                continue;
            }

            object? defaultValue = property.GetValue(new AppSettings());
            object? expected = property.GetValue(source);
            object? actual = property.GetValue(target);
            Assert.False(Same(defaultValue, expected), $"Test must set {property.Name} to a non-default value.");
            Assert.True(Same(expected, actual), $"CopyFrom does not copy {property.Name}.");
        }
    }

    [Fact]
    public void BackupRestoreKeepsUpdateBookkeeping()
    {
        var target = new AppSettings
        {
            LastUpdateCheckUtc = DateTimeOffset.UnixEpoch,
            SkippedUpdateVersion = "1.2.3"
        };
        target.CopyFrom(new AppSettings());
        Assert.Equal(DateTimeOffset.UnixEpoch, target.LastUpdateCheckUtc);
        Assert.Equal("1.2.3", target.SkippedUpdateVersion);
    }

    [Fact]
    public void UpdateCheckIsOnByDefault()
    {
        Assert.True(new AppSettings().CheckForUpdates);
    }

    [Fact]
    public void NormalizeRepairsSoundSettings()
    {
        var settings = new AppSettings { SoundVariant = "bogus", SoundVolume = 250 };
        AppSettingsService.Normalize(settings);
        Assert.Equal("sound1", settings.SoundVariant);
        Assert.Equal(100, settings.SoundVolume);
    }

    private static bool Same(object? a, object? b)
    {
        if (a is HotkeySettings ha && b is HotkeySettings hb)
        {
            return ha.ConvertLastWord == hb.ConvertLastWord;
        }

        if (a is List<string> la && b is List<string> lb)
        {
            return la.SequenceEqual(lb);
        }

        return Equals(a, b);
    }

    // ── localization ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void EverySettingsPageHasTitleAndDescription(string culture)
    {
        var resources = new ResourceManager("LayoutFixer.Resources.Strings", typeof(AppSettings).Assembly);
        CultureInfo info = CultureInfo.GetCultureInfo(culture);
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            Assert.False(string.IsNullOrEmpty(resources.GetString("SettingsPage" + page, info)), page.ToString());
            Assert.False(string.IsNullOrEmpty(resources.GetString("SettingsPage" + page + "Description", info)), page.ToString());
        }
    }

    // ── user dictionary editing ──────────────────────────────────────────────

    [Fact]
    public void DictionaryReplaceEditsAndMovesWords()
    {
        string folder = Path.Combine(Path.GetTempPath(), "LayoutFixer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var dictionary = new UserDictionary(Path.Combine(folder, "user-dictionary.json"));
            int changes = 0;
            dictionary.Changed += (_, _) => changes++;

            Assert.True(dictionary.Add(ConvertedLanguage.English, "github"));
            Assert.True(dictionary.Replace(ConvertedLanguage.English, "github", ConvertedLanguage.English, "GitHub"));
            Assert.Equal(new[] { "GitHub" }, dictionary.GetWords(ConvertedLanguage.English));

            // Move to the other list.
            Assert.True(dictionary.Replace(ConvertedLanguage.English, "GitHub", ConvertedLanguage.Russian, "пшерги"));
            Assert.Empty(dictionary.GetWords(ConvertedLanguage.English));
            Assert.True(dictionary.ContainsRussian("пшерги"));

            // A duplicate target keeps the original word.
            Assert.True(dictionary.Add(ConvertedLanguage.Russian, "привет"));
            Assert.False(dictionary.Replace(ConvertedLanguage.Russian, "привет", ConvertedLanguage.Russian, "ПШЕРГИ"));
            Assert.True(dictionary.ContainsRussian("привет"));

            // Removal is case-insensitive in both the set and the visible list.
            Assert.Equal(1, dictionary.RemoveRange(ConvertedLanguage.Russian, new[] { "ПРИВЕТ" }));
            Assert.Equal(new[] { "пшерги" }, dictionary.GetWords(ConvertedLanguage.Russian));
            Assert.True(dictionary.Remove(ConvertedLanguage.Russian, "ПШЕРГИ"));
            Assert.Empty(dictionary.GetWords(ConvertedLanguage.Russian));

            Assert.Equal(6, changes);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("привет", "Russian")]
    [InlineData("GitHub", "English")]
    [InlineData("ёлка", "Russian")]
    [InlineData("iPhoneФ", "Unknown")]
    [InlineData("123", "Unknown")]
    public void NewDictionaryWordGoesToTheListOfItsAlphabet(string word, string expected)
    {
        Assert.Equal(expected, UserDictionaryForm.DetectWordLanguage(word).ToString());
    }
}

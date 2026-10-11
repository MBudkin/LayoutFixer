using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using LayoutFixer.Localization;

namespace LayoutFixer.Settings;

internal enum RememberMode
{
    Remember = 0,
    Ask = 1,
    Never = 2
}

internal enum TrayIconMode
{
    Program = 0,
    Language = 1,
    Flag = 2
}

internal sealed class AppSettings
{
    public string Language { get; set; } = LanguageCodes.System;

    public HotkeySettings Hotkeys { get; set; } = new();

    public bool ShowNotifications { get; set; } = true;

    public TrayIconMode TrayIconMode { get; set; } = TrayIconMode.Language;

    // How long a tray notification / popup stays on screen, in seconds.
    public int NotificationDurationSec { get; set; } = 10;

    public bool PlaySoundOnConversion { get; set; } = true;

    // Which sound plays on conversion: "sound1", "sound2" or "custom".
    public string SoundVariant { get; set; } = "sound1";

    // Path to a user-selected WAV when SoundVariant == "custom".
    public string CustomSoundPath { get; set; } = string.Empty;

    // Playback volume, 0..100.
    public int SoundVolume { get; set; } = 100;

    public bool StartWithWindows { get; set; }

    public bool ClearBufferAfterConversion { get; set; }

    public bool AutoMode { get; set; } = true;

    // "Claude b ChatGPT" -> "Claude и ChatGPT": a lowercase single letter that
    // is a Russian word (в, а, я, и, с, у, о, к) is converted even right after
    // an English word. Off: it stays after an ordinary English word ("plan b").
    public bool AlwaysConvertSingleLetters { get; set; } = true;

    // Capitalize the first word after `. `, `! ` or `? `. This is independent
    // of layout auto-correction and can be disabled separately.
    public bool AutoCapitalizeSentences { get; set; }

    public bool FixAccidentalCapsLock { get; set; } = true;

    // "ПРивет" -> "Привет": two capitals at the start of a word followed by
    // lowercase letters (Shift released too late).
    public bool FixTwoInitialCapitals { get; set; } = true;

    // Automatic switching, auto-replacement and case fixes stay off while a
    // borderless full-screen window (a game, a video, a presentation) is in
    // front. Manual hotkeys keep working.
    public bool DisableInFullscreenApps { get; set; } = true;

    // Executable names ("game.exe", "mstsc.exe") where automatic switching,
    // auto-replacement and case fixes are disabled. Manual hotkeys keep working.
    public List<string> ExcludedApps { get; set; } = new();

    public bool InvertEachCharacterCase { get; set; }

    public bool AutoReplace { get; set; } = true;

    public bool TabAsWordSeparator { get; set; } = true;

    // When true, a user-replacement trigger may be wrapped in punctuation
    // ("спс," -> "спасибо,") — the surrounding punctuation is stripped before
    // the lookup and re-applied to the expansion.
    public bool ReplacementsRespectPunctuation { get; set; } = true;

    // How a word corrected by the last-word hotkey (Pause) is remembered:
    // Remember -> silent add to the user dictionary;
    // Ask      -> ask via a popup;
    // Never    -> do nothing.
    public RememberMode ManualLearnMode { get; set; } = RememberMode.Ask;

    // How a word corrected from a selection (Shift+Pause) is remembered:
    // Ask   -> ask via a popup;
    // Never -> do nothing.
    public RememberMode SelectionLearnMode { get; set; } = RememberMode.Never;

    public bool EnableDebugLog { get; set; }

    // Once a day ask GitHub whether a newer release exists. Only the public
    // "latest release" metadata is requested; nothing typed is ever sent.
    public bool CheckForUpdates { get; set; } = true;

    // When the last successful automatic or manual check happened.
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    // A release the user chose to skip: automatic checks stay quiet about it
    // (a newer one is announced again). Manual checks always show it.
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    /// <summary>
    /// Copies every user-facing setting from <paramref name="source"/> (a
    /// restored backup). Update bookkeeping (last check, skipped version)
    /// belongs to this computer and is kept. AppSettingsTests verifies that a
    /// newly added property is not forgotten here.
    /// </summary>
    public void CopyFrom(AppSettings source)
    {
        Language = source.Language;
        Hotkeys = source.Hotkeys ?? HotkeySettings.Defaults();
        ShowNotifications = source.ShowNotifications;
        TrayIconMode = source.TrayIconMode;
        NotificationDurationSec = source.NotificationDurationSec;
        PlaySoundOnConversion = source.PlaySoundOnConversion;
        SoundVariant = source.SoundVariant;
        CustomSoundPath = source.CustomSoundPath;
        SoundVolume = source.SoundVolume;
        StartWithWindows = source.StartWithWindows;
        ClearBufferAfterConversion = source.ClearBufferAfterConversion;
        AutoMode = source.AutoMode;
        AlwaysConvertSingleLetters = source.AlwaysConvertSingleLetters;
        AutoCapitalizeSentences = source.AutoCapitalizeSentences;
        FixAccidentalCapsLock = source.FixAccidentalCapsLock;
        FixTwoInitialCapitals = source.FixTwoInitialCapitals;
        DisableInFullscreenApps = source.DisableInFullscreenApps;
        ExcludedApps = new List<string>(source.ExcludedApps ?? new List<string>());
        InvertEachCharacterCase = source.InvertEachCharacterCase;
        AutoReplace = source.AutoReplace;
        TabAsWordSeparator = source.TabAsWordSeparator;
        ReplacementsRespectPunctuation = source.ReplacementsRespectPunctuation;
        ManualLearnMode = source.ManualLearnMode;
        SelectionLearnMode = source.SelectionLearnMode;
        EnableDebugLog = source.EnableDebugLog;
        CheckForUpdates = source.CheckForUpdates;
    }

    // Legacy bridge for migrating pre-existing profiles. Old settings.json files
    // store "autoAddToUserDictionary" (bool); this nullable property captures it
    // during deserialization and is converted to ManualLearnMode in Normalize.
    // JsonIgnore(WhenWritingNull) keeps it out of the written file: once migrated
    // it is cleared to null and never persisted again.
    [JsonPropertyName("autoAddToUserDictionary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoAddToUserDictionaryLegacy { get; set; }
}

internal sealed class HotkeySettings
{
    public string ConvertLastWord { get; set; } = "Pause";

    public string ConvertSelection { get; set; } = "Shift+Pause";

    public string ToggleEnabled { get; set; } = "Ctrl+Shift+Space";

    public string ToggleCase { get; set; } = "Ctrl+Shift+F9";

    public string Transliterate { get; set; } = "Ctrl+Shift+F10";

    public static HotkeySettings Defaults()
    {
        return new HotkeySettings();
    }
}

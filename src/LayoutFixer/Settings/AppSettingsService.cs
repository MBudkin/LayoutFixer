using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LayoutFixer.Localization;
using LayoutFixer.Services;

namespace LayoutFixer.Settings;

internal sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        // Serialize enums (RememberMode) as readable strings instead of numbers,
        // so settings.json stays human-friendly and survives enum reordering.
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettingsService()
    {
        SettingsPath = AppPaths.File("settings.json");
    }

    public string SettingsPath { get; }

    public bool LastLoadHadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadHadError = false;

        if (!File.Exists(SettingsPath) && !File.Exists(AtomicFile.BackupPath(SettingsPath)))
        {
            return new AppSettings();
        }

        AppSettings? settings = AtomicFile.Load(
            SettingsPath,
            json => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings(),
            out bool recovered);
        if (settings is null)
        {
            LastLoadHadError = true;
            return new AppSettings();
        }

        LastLoadHadError = recovered;
        Normalize(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(SettingsPath, json);
    }

    // Lowercase file names without paths, ".exe" appended when missing,
    // duplicates and blanks removed, original order kept.
    public static List<string> NormalizeAppList(IEnumerable<string>? apps)
    {
        var result = new List<string>();
        if (apps is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in apps)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string name = Path.GetFileName(raw.Trim().Trim('"')).ToLowerInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            if (!name.EndsWith(".exe", StringComparison.Ordinal))
            {
                name += ".exe";
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    // Public for the backup import, which deserializes settings itself.
    public static void Normalize(AppSettings settings)
    {
        settings.Language = LanguageCodes.Normalize(settings.Language);
        if (!Enum.IsDefined(settings.TrayIconMode))
        {
            settings.TrayIconMode = TrayIconMode.Language;
        }

        settings.Hotkeys ??= HotkeySettings.Defaults();

        var defaults = HotkeySettings.Defaults();
        if (string.IsNullOrWhiteSpace(settings.Hotkeys.ConvertLastWord))
        {
            settings.Hotkeys.ConvertLastWord = defaults.ConvertLastWord;
        }

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.ConvertSelection))
        {
            settings.Hotkeys.ConvertSelection = defaults.ConvertSelection;
        }

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.ToggleEnabled))
        {
            settings.Hotkeys.ToggleEnabled = defaults.ToggleEnabled;
        }

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.ToggleCase) ||
            string.Equals(settings.Hotkeys.ToggleCase, "Ctrl+Pause", StringComparison.OrdinalIgnoreCase))
        {
            settings.Hotkeys.ToggleCase = defaults.ToggleCase;
        }

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.Transliterate) ||
            string.Equals(settings.Hotkeys.Transliterate, "Alt+Pause", StringComparison.OrdinalIgnoreCase))
        {
            settings.Hotkeys.Transliterate = defaults.Transliterate;
        }

        settings.ExcludedApps = NormalizeAppList(settings.ExcludedApps);
        settings.SoundVariant = settings.SoundVariant is "sound1" or "sound2" or "custom"
            ? settings.SoundVariant
            : "sound1";
        settings.CustomSoundPath ??= string.Empty;
        settings.SoundVolume = Math.Clamp(settings.SoundVolume, 0, 100);
        settings.SkippedUpdateVersion ??= string.Empty;

        // Clamp the notification duration to a sane window.
        if (settings.NotificationDurationSec < 1)
        {
            settings.NotificationDurationSec = 1;
        }
        else if (settings.NotificationDurationSec > 300)
        {
            settings.NotificationDurationSec = 300;
        }

        // Migrate the legacy "autoAddToUserDictionary" bool into the new
        // ManualLearnMode, but only on the very first migration: once the user
        // has a ManualLearnMode value in the file (Legacy == null on load),
        // later changes to the legacy key are ignored.
        if (settings.AutoAddToUserDictionaryLegacy.HasValue)
        {
            settings.ManualLearnMode = settings.AutoAddToUserDictionaryLegacy.Value
                ? RememberMode.Remember
                : RememberMode.Never;
        }

        if (!Enum.IsDefined(settings.ManualLearnMode))
        {
            settings.ManualLearnMode = RememberMode.Ask;
        }

        if (!Enum.IsDefined(settings.SelectionLearnMode))
        {
            settings.SelectionLearnMode = RememberMode.Never;
        }

        // Selection learning has no silent "Remember" UI option; clamp legacy or
        // hand-edited values to the safer default.
        if (settings.SelectionLearnMode == RememberMode.Remember)
        {
            settings.SelectionLearnMode = RememberMode.Never;
        }

        // Never persist the legacy bridge: it has served its purpose and the new
        // keys fully describe the intent from now on.
        settings.AutoAddToUserDictionaryLegacy = null;
    }
}

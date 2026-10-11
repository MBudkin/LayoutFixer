using System;
using System.Globalization;
using System.Resources;

namespace LayoutFixer.Localization;

internal static class Localizer
{
    private static readonly ResourceManager ResourceManager =
        new("LayoutFixer.Resources.Strings", typeof(Localizer).Assembly);

    public static event EventHandler? LanguageChanged;

    public static string SelectedLanguage { get; private set; } = LanguageCodes.System;

    public static CultureInfo EffectiveCulture { get; private set; } = ResolveCulture(LanguageCodes.System);

    public static void Apply(string? language)
    {
        string normalizedLanguage = LanguageCodes.Normalize(language);
        CultureInfo resolvedCulture = ResolveCulture(normalizedLanguage);

        bool changed =
            !StringComparer.Ordinal.Equals(SelectedLanguage, normalizedLanguage) ||
            !StringComparer.Ordinal.Equals(EffectiveCulture.Name, resolvedCulture.Name);

        SelectedLanguage = normalizedLanguage;
        EffectiveCulture = resolvedCulture;
        CultureInfo.CurrentCulture = resolvedCulture;
        CultureInfo.CurrentUICulture = resolvedCulture;
        CultureInfo.DefaultThreadCurrentCulture = resolvedCulture;
        CultureInfo.DefaultThreadCurrentUICulture = resolvedCulture;

        if (changed)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static string Get(string key)
    {
        return ResourceManager.GetString(key, EffectiveCulture) ?? $"[[{key}]]";
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(EffectiveCulture, Get(key), args);
    }

    private static CultureInfo ResolveCulture(string language)
    {
        return language switch
        {
            LanguageCodes.Russian => CultureInfo.GetCultureInfo(LanguageCodes.Russian),
            LanguageCodes.English => CultureInfo.GetCultureInfo(LanguageCodes.English),
            _ => ResolveSystemCulture()
        };
    }

    private static CultureInfo ResolveSystemCulture()
    {
        string windowsLanguage = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;

        return windowsLanguage switch
        {
            LanguageCodes.Russian => CultureInfo.GetCultureInfo(LanguageCodes.Russian),
            LanguageCodes.English => CultureInfo.GetCultureInfo(LanguageCodes.English),
            _ => CultureInfo.GetCultureInfo(LanguageCodes.English)
        };
    }
}

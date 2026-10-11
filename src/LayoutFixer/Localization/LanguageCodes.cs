namespace LayoutFixer.Localization;

internal static class LanguageCodes
{
    public const string System = "system";
    public const string Russian = "ru";
    public const string English = "en";

    public static string Normalize(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            Russian => Russian,
            English => English,
            _ => System
        };
    }
}

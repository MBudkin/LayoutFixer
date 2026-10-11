using System;

namespace LayoutFixer.Input;

internal static class LayoutProbe
{
    private const int PrimaryLanguageMask = 0x03FF;
    private const int PrimaryEnglish = 0x09;
    private const int PrimaryRussian = 0x19;

    public static ushort GetCurrentLayoutId()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return 0;
            }

            uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            IntPtr layout = NativeMethods.GetKeyboardLayout(threadId);
            return (ushort)((long)layout & 0xFFFF);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Language of the foreground window's current keyboard layout.</summary>
    public static ConvertedLanguage GetCurrentLanguage() => LanguageOf(GetCurrentLayoutId());

    // By PRIMARY language, so US, UK, Canadian, ... English layouts all count
    // as English (they share the letter keys the converter maps). Any other
    // language (Ukrainian, Belarusian, German, ...) is Unknown: automatic
    // conversion must not touch text typed in a third layout.
    public static ConvertedLanguage LanguageOf(ushort langId)
    {
        return (langId & PrimaryLanguageMask) switch
        {
            PrimaryEnglish => ConvertedLanguage.English,
            PrimaryRussian => ConvertedLanguage.Russian,
            _ => ConvertedLanguage.Unknown
        };
    }
}

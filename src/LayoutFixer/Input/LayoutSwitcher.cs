using System;
using System.Runtime.InteropServices;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

internal static class LayoutSwitcher
{
    private const string EnglishLayoutId = "00000409";
    private const string RussianLayoutId = "00000419";
    private const ushort EnglishLangId = 0x0409;
    private const ushort RussianLangId = 0x0419;

    public static void SwitchTo(ConvertedLanguage language)
    {
        ushort? targetLangId = language switch
        {
            ConvertedLanguage.English => EnglishLangId,
            ConvertedLanguage.Russian => RussianLangId,
            _ => null
        };

        if (targetLangId is null)
        {
            Logger.Log($"SwitchTo: language={language} -> nothing to do");
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            Logger.Log("SwitchTo: no foreground window");
            return;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        IntPtr currentLayout = NativeMethods.GetKeyboardLayout(threadId);
        ushort currentLangId = (ushort)((long)currentLayout & 0xFFFF);

        Logger.Log($"SwitchTo: language={language} target=0x{targetLangId.Value:X4} current=0x{currentLangId:X4} hkl=0x{(long)currentLayout:X}");

        if (LayoutProbe.LanguageOf(currentLangId) == language)
        {
            Logger.Log("SwitchTo: already on target layout");
            return;
        }

        string layoutId = targetLangId.Value == EnglishLangId ? EnglishLayoutId : RussianLayoutId;

        try
        {
            IntPtr hkl = FindOrLoadLayout(targetLangId.Value, layoutId);
            Logger.Log($"SwitchTo: found/loaded hkl=0x{(long)hkl:X}");
            if (hkl != IntPtr.Zero)
            {
                PostToFocusOrForeground(foreground, threadId, hkl);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SwitchTo PostMessage exception: {ex.Message}");
        }
    }

    public static IntPtr GetLayout(ConvertedLanguage language)
    {
        return language switch
        {
            ConvertedLanguage.English => FindOrLoadLayout(EnglishLangId, EnglishLayoutId),
            ConvertedLanguage.Russian => FindOrLoadLayout(RussianLangId, RussianLayoutId),
            _ => NativeMethods.GetKeyboardLayout(0)
        };
    }

    // Fast fire-and-forget path for live (mid-word) conversions: there is no
    // foreground re-verification and no waiting, because the typed replacement
    // is injected as layout-independent Unicode and only the next physical
    // keystroke needs the new layout. The low-level keyboard hook is
    // synchronous, so by the time the user presses the next key the posted
    // WM_INPUTLANGCHANGEREQUEST has been processed. SwitchTo is already
    // non-blocking (it only PostMessages), so this is a thin alias that
    // documents intent at the call site.
    public static void PostSwitchTo(ConvertedLanguage language)
    {
        SwitchTo(language);
    }

    private static void PostToFocusOrForeground(IntPtr foreground, uint threadId, IntPtr hkl)
    {
        IntPtr target = foreground;

        if (threadId != 0)
        {
            var info = new NativeMethods.GuiThreadInfo
            {
                CbSize = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
            };

            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.HwndFocus != IntPtr.Zero)
            {
                target = info.HwndFocus;
            }
        }

        NativeMethods.PostMessage(
            target,
            NativeMethods.WmInputlangchangerequest,
            (IntPtr)NativeMethods.InputlangchangeSyscharset,
            hkl);
    }

    // Prefers an installed layout of exactly this language id, then any
    // installed layout of the same language (UK/Canadian English, ...). Only
    // when the user has no such layout at all is the standard one loaded.
    private static IntPtr FindOrLoadLayout(ushort langId, string layoutId)
    {
        try
        {
            uint count = NativeMethods.GetKeyboardLayoutList(0, null);
            if (count > 0)
            {
                var layouts = new IntPtr[count];
                NativeMethods.GetKeyboardLayoutList(count, layouts);
                IntPtr sameLanguage = IntPtr.Zero;
                ConvertedLanguage wanted = LayoutProbe.LanguageOf(langId);
                foreach (IntPtr hkl in layouts)
                {
                    ushort candidate = (ushort)((long)hkl & 0xFFFF);
                    if (candidate == langId)
                    {
                        return hkl;
                    }

                    if (sameLanguage == IntPtr.Zero && LayoutProbe.LanguageOf(candidate) == wanted)
                    {
                        sameLanguage = hkl;
                    }
                }

                if (sameLanguage != IntPtr.Zero)
                {
                    return sameLanguage;
                }
            }
        }
        catch
        {
            // Fall through to LoadKeyboardLayout.
        }

        return NativeMethods.LoadKeyboardLayout(layoutId, 0);
    }
}

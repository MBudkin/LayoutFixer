using System;
using System.Runtime.InteropServices;
using System.Threading;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Reads the active text selection in the focused window by trying several copy
/// mechanisms in order, capturing the clipboard, and restoring the original
/// clipboard contents afterwards.
/// </summary>
/// <remarks>
/// Uses the raw Win32 clipboard API (not WinForms Clipboard) so it can run on a
/// background MTA thread without an STA requirement, and never pumps messages on
/// the calling thread. This is important because callers must NOT be the thread
/// that owns the global low-level keyboard/mouse hooks: blocking that thread
/// freezes input system-wide until the clipboard capture completes.
///
/// Different editors honor different copy mechanisms, so a guarded cascade is
/// used: WM_COPY first, then Ctrl+Insert. Ctrl+C and Ctrl+Shift+C are deliberately
/// avoided because some editors copy a line, link or path when nothing is
/// selected, which is unsafe for a selection-replacement command.
/// </remarks>
internal static class SelectionReader
{
    private const int VkControl = 0x11;
    private const int VkInsert = 0x2D;

    // WM_COPY is synchronous (SendMessage), so the clipboard is populated by the
    // time the call returns; a few short polls are enough.
    private const int MessagePollAttempts = 6;

    // Injected keystrokes are processed asynchronously, so poll a bit longer.
    private const int KeyboardPollAttempts = 30;

    private const int PollDelayMs = 5;
    private const int ClipboardOpenAttempts = 10;
    private const int ClipboardOpenRetryDelayMs = 5;
    private const int SendTimeoutMs = 500;

    public static bool TryGetSelectionSafely(out string selection)
    {
        selection = string.Empty;

        // Every format, not just text: an image or a file list the user had
        // copied must survive the round-trip.
        ClipboardSnapshot backup = ClipboardSnapshot.Capture();

        string? captured = null;
        try
        {
            captured ??= TryCaptureByMessage();
            captured ??= TryCaptureByKeys(BuildCtrlInsert(), "Ctrl+Insert");
        }
        finally
        {
            backup.Restore();
        }

        if (string.IsNullOrEmpty(captured))
        {
            Logger.Log("SelectionReader: no text captured");
            return false;
        }

        selection = captured;
        Logger.Log(Logger.Format("SelectionReader: captured", selection));
        return true;
    }

    private static string? TryCaptureByMessage()
    {
        IntPtr hwnd = GetFocusHandle();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!EmptyClipboardSafe())
        {
            return null;
        }

        try
        {
            NativeMethods.SendMessageTimeout(
                hwnd,
                NativeMethods.WmCopy,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                SendTimeoutMs,
                out IntPtr _);
        }
        catch (Exception ex)
        {
            Logger.Log($"SelectionReader: WM_COPY failed: {ex.Message}");
            return null;
        }

        return PollForText(MessagePollAttempts);
    }

    private static string? TryCaptureByKeys(NativeMethods.Input[] inputs, string label)
    {
        if (!EmptyClipboardSafe())
        {
            return null;
        }

        uint sent;
        try
        {
            sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        }
        catch (Exception ex)
        {
            Logger.Log($"SelectionReader: {label} failed: {ex.Message}");
            return null;
        }

        if (sent != inputs.Length)
        {
            Logger.Log($"SelectionReader: {label} send incomplete sent={sent}");
            return null;
        }

        Logger.Log($"SelectionReader: SendInput {label} sent={sent}");
        return PollForText(KeyboardPollAttempts);
    }

    private static string? PollForText(int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            Thread.Sleep(PollDelayMs);
            string? text = TryGetUnicodeText();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    private static IntPtr GetFocusHandle()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var info = new NativeMethods.GuiThreadInfo
        {
            CbSize = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.HwndFocus != IntPtr.Zero)
        {
            return info.HwndFocus;
        }

        return foreground;
    }

    private static NativeMethods.Input[] BuildCtrlInsert() =>
    [
        MakeKey(VkControl, extended: false, down: true),
        MakeKey(VkInsert, extended: true, down: true),
        MakeKey(VkInsert, extended: true, down: false),
        MakeKey(VkControl, extended: false, down: false)
    ];

    private static NativeMethods.Input MakeKey(int vk, bool extended, bool down)
    {
        uint flags = down ? 0 : NativeMethods.KeyeventfKeyup;
        if (extended)
        {
            flags |= NativeMethods.KeyeventfExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeyboardInput
                {
                    WVk = (ushort)vk,
                    WScan = 0,
                    DwFlags = flags
                }
            }
        };
    }

    private static string? TryGetUnicodeText()
    {
        try
        {
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText))
            {
                return null;
            }

            if (!TryOpenClipboard())
            {
                return null;
            }

            try
            {
                IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr ptr = NativeMethods.GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SelectionReader: get text failed: {ex.Message}");
            return null;
        }
    }

    private static bool EmptyClipboardSafe()
    {
        if (!TryOpenClipboard())
        {
            Logger.Log("SelectionReader: OpenClipboard(empty) failed");
            return false;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (int i = 0; i < ClipboardOpenAttempts; i++)
        {
            if (NativeMethods.OpenClipboard(ClipboardSnapshot.OwnerWindow))
            {
                return true;
            }

            Thread.Sleep(ClipboardOpenRetryDelayMs);
        }

        return false;
    }
}

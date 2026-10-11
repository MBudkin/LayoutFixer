using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LayoutFixer.Input;

internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hookHandle;
    private long _lastEventTick = Environment.TickCount64;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public event EventHandler<KeyboardObservedEventArgs>? KeyObserved;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _lastEventTick = Environment.TickCount64;
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _proc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // Environment.TickCount64 of the last callback (any event, injected or
    // not). Windows silently removes a low-level hook whose callback once
    // exceeded LowLevelHooksTimeout; the tray context compares this with
    // GetLastInputInfo to notice that and reinstall the hook.
    public long LastEventTick => System.Threading.Interlocked.Read(ref _lastEventTick);

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public void Restart()
    {
        Dispose();
        Start();
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _lastEventTick = Environment.TickCount64;
        try
        {
            bool isKeyDown = wParam == NativeMethods.WmKeydown || wParam == NativeMethods.WmSyskeydown;
            if (nCode >= 0 && isKeyDown)
            {
                var data = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);
                bool injected = (data.Flags & NativeMethods.LlkhfInjected) != 0;
                if (!injected)
                {
                    var args = new KeyboardObservedEventArgs((int)data.VkCode, (int)data.ScanCode);
                    KeyObserved?.Invoke(this, args);
                    // A live layout conversion handled this keystroke entirely:
                    // swallow it so the OS does not also deliver the original
                    // character into the focused window on top of the injected
                    // replacement. Returning a non-zero value from a
                    // low-level keyboard hook suppresses the keystroke.
                    if (args.Suppress)
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // Keyboard hook callbacks must never throw into user32.
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }
}

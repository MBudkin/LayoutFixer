using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

internal sealed class MouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc;

    private IntPtr _hookHandle;
    private long _lastEventTick = Environment.TickCount64;
    private NativeMethods.Point _leftButtonStart;
    private NativeMethods.Point _lastLeftButtonDown;
    private int _lastLeftButtonDownTick;
    private bool _leftButtonDown;
    private bool _leftButtonDragged;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public event EventHandler? Clicked;
    public event EventHandler? SelectionGesture;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _lastEventTick = Environment.TickCount64;
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _proc, IntPtr.Zero, 0);
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
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var data = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(lParam);
                bool injected = (data.Flags & NativeMethods.LlmhfInjected) != 0;
                if (!injected)
                {
                    if (msg == NativeMethods.WmLbuttondown)
                    {
                        Logger.Log($"MouseHook: click at ({data.Pt.X},{data.Pt.Y}) msg=0x{msg:X3}");
                        Clicked?.Invoke(this, EventArgs.Empty);
                        HandleLeftButtonDown(data.Pt);
                    }
                    else if (msg == NativeMethods.WmMousemove && _leftButtonDown)
                    {
                        if (DistanceExceeded(_leftButtonStart, data.Pt))
                        {
                            _leftButtonDragged = true;
                        }
                    }
                    else if (msg == NativeMethods.WmLbuttonup)
                    {
                        if (_leftButtonDown && _leftButtonDragged)
                        {
                            Logger.Log("MouseHook: text-selection drag gesture");
                            SelectionGesture?.Invoke(this, EventArgs.Empty);
                        }

                        _leftButtonDown = false;
                        _leftButtonDragged = false;
                    }
                    else if (msg == NativeMethods.WmRbuttondown ||
                             msg == NativeMethods.WmMbuttondown ||
                             msg == NativeMethods.WmXbuttondown)
                    {
                        Logger.Log($"MouseHook: click at ({data.Pt.X},{data.Pt.Y}) msg=0x{msg:X3}");
                        Clicked?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        catch
        {
            // Mouse hook callbacks must never throw into user32.
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void HandleLeftButtonDown(NativeMethods.Point point)
    {
        int now = Environment.TickCount;
        int elapsed = unchecked(now - _lastLeftButtonDownTick);
        bool isDoubleClick = _lastLeftButtonDownTick != 0 &&
                             elapsed >= 0 &&
                             elapsed <= NativeMethods.GetDoubleClickTime() &&
                             !DistanceExceeded(_lastLeftButtonDown, point);

        _leftButtonStart = point;
        _leftButtonDown = true;
        _leftButtonDragged = false;
        _lastLeftButtonDown = point;
        _lastLeftButtonDownTick = now;

        if (isDoubleClick)
        {
            Logger.Log("MouseHook: text-selection double-click gesture");
            SelectionGesture?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool DistanceExceeded(NativeMethods.Point first, NativeMethods.Point second)
    {
        const int threshold = 4;
        return Math.Abs(first.X - second.X) >= threshold ||
               Math.Abs(first.Y - second.Y) >= threshold;
    }
}

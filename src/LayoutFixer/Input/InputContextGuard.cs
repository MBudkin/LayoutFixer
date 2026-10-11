using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Knows where the user is typing, so the keyboard hook can decide cheaply
/// whether automatic edits are allowed: not in password fields, not in
/// excluded programs, not in full-screen games/videos.
/// </summary>
/// <remarks>
/// The hook itself must stay fast, so everything expensive happens elsewhere:
/// foreground/focus changes arrive as out-of-context WinEvents on the UI
/// thread, and the password check of the focused element (UI Automation is a
/// cross-process COM call) runs on a dedicated background thread. The hook
/// only reads the cached results. Until a fresh password probe has finished,
/// the field is treated as "maybe a password" and automatic edits wait.
/// </remarks>
internal sealed class InputContextGuard : IDisposable
{
    private const int ProbeDebounceMs = 30;
    private const int PendingTimeoutMs = 800;
    private const int FullscreenCacheMs = 1000;

    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _focusHook;
    private readonly Thread _probeThread;
    private readonly AutoResetEvent _probeSignal = new(false);
    private readonly object _gate = new();
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private volatile bool _disposed;
    private volatile bool _passwordFocus;
    private long _probeRequestedTick;
    private long _probeCompletedTick;
    private IntPtr _foreground;
    private string _foregroundProcess = string.Empty;
    private IntPtr _fullscreenWindow;
    private bool _fullscreenCached;
    private long _fullscreenCheckedTick;

    public InputContextGuard()
    {
        _winEventProc = OnWinEvent;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        _focusHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectFocus,
            NativeMethods.EventObjectFocus,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);

        _probeThread = new Thread(ProbeLoop)
        {
            IsBackground = true,
            Name = "LayoutFixer focus probe"
        };
        _probeThread.SetApartmentState(ApartmentState.MTA);
        _probeThread.Start();

        UpdateForeground(NativeMethods.GetForegroundWindow());
    }

    /// <summary>Raised on the probe thread when the focused field turns out to be a password field.</summary>
    public event EventHandler? PasswordFieldFocused;

    /// <summary>Executable name of the last foreground window that is not LayoutFixer or the taskbar.</summary>
    public string LastExternalProcess { get; private set; } = string.Empty;

    /// <summary>True when the focused control is (or may still turn out to be) a password field.</summary>
    public bool IsPasswordFieldFocused => _passwordFocus;

    public bool IsProbePending
    {
        get
        {
            long requested = Interlocked.Read(ref _probeRequestedTick);
            long completed = Interlocked.Read(ref _probeCompletedTick);
            return requested > completed &&
                   Environment.TickCount64 - requested < PendingTimeoutMs;
        }
    }

    /// <summary>
    /// Re-checks the focused control soon. Called on focus WinEvents and on
    /// input that usually moves focus without one (mouse clicks, Tab).
    /// </summary>
    public void RequestProbe()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _probeRequestedTick, Environment.TickCount64);
        _probeSignal.Set();
    }

    /// <summary>
    /// Returns a reason when automatic edits must not happen right now, or
    /// null when they may. Cheap: safe to call from the keyboard hook.
    /// </summary>
    public string? GetAutoBlockReason(IReadOnlyCollection<string> excludedApps, bool disableInFullscreen)
    {
        if (_passwordFocus)
        {
            return "password field";
        }

        if (IsProbePending)
        {
            return "focus check pending";
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _foreground)
        {
            UpdateForeground(foreground);
        }

        if (excludedApps.Count > 0 && IsExcluded(_foregroundProcess, excludedApps))
        {
            return "excluded program " + _foregroundProcess;
        }

        if (disableInFullscreen && IsFullscreen(foreground))
        {
            return "full-screen window";
        }

        return null;
    }

    public static bool IsExcluded(string processName, IReadOnlyCollection<string> excludedApps)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (string app in excludedApps)
        {
            if (string.Equals(app, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
        }

        if (_focusHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_focusHook);
        }

        _probeSignal.Set();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (eventType == NativeMethods.EventSystemForeground)
            {
                UpdateForeground(hwnd);
            }

            RequestProbe();
        }
        catch
        {
            // WinEvent callbacks must never throw.
        }
    }

    private void UpdateForeground(IntPtr hwnd)
    {
        _foreground = hwnd;
        _fullscreenCheckedTick = 0;
        if (hwnd == IntPtr.Zero)
        {
            _foregroundProcess = string.Empty;
            return;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        _foregroundProcess = processId == 0 ? string.Empty : GetProcessName(processId);
        if (processId != _ownProcessId && !IsTaskbarWindow(hwnd) && _foregroundProcess.Length > 0)
        {
            LastExternalProcess = _foregroundProcess;
        }
    }

    private string GetProcessName(uint processId)
    {
        lock (_gate)
        {
            if (_processNames.TryGetValue(processId, out string? cached))
            {
                return cached;
            }
        }

        string name = string.Empty;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process != IntPtr.Zero)
        {
            try
            {
                var builder = new StringBuilder(1024);
                uint size = (uint)builder.Capacity;
                if (NativeMethods.QueryFullProcessImageName(process, 0, builder, ref size))
                {
                    name = Path.GetFileName(builder.ToString()).ToLowerInvariant();
                }
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        lock (_gate)
        {
            // Process ids are reused; keep the cache small and fresh.
            if (_processNames.Count > 256)
            {
                _processNames.Clear();
            }

            _processNames[processId] = name;
        }

        return name;
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        string className = GetClassName(hwnd);
        return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow" or "Progman" or "WorkerW";
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    // A borderless window covering its whole monitor: games, video players,
    // presentations, browsers in F11 mode. Maximised normal windows keep
    // their caption and do not count.
    private bool IsFullscreen(IntPtr hwnd)
    {
        long now = Environment.TickCount64;
        if (hwnd == _fullscreenWindow && now - _fullscreenCheckedTick < FullscreenCacheMs)
        {
            return _fullscreenCached;
        }

        bool fullscreen = false;
        if (hwnd != IntPtr.Zero &&
            hwnd != NativeMethods.GetShellWindow() &&
            hwnd != NativeMethods.GetDesktopWindow() &&
            !IsTaskbarWindow(hwnd))
        {
            long style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle);
            if ((style & NativeMethods.WsCaption) != NativeMethods.WsCaption &&
                NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect window))
            {
                IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
                var info = new NativeMethods.MonitorInfo
                {
                    CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
                };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    NativeMethods.Rect screen = info.RcMonitor;
                    fullscreen = window.Left <= screen.Left && window.Top <= screen.Top &&
                                 window.Right >= screen.Right && window.Bottom >= screen.Bottom;
                }
            }
        }

        _fullscreenWindow = hwnd;
        _fullscreenCached = fullscreen;
        _fullscreenCheckedTick = now;
        return fullscreen;
    }

    private void ProbeLoop()
    {
        while (!_disposed)
        {
            _probeSignal.WaitOne();
            if (_disposed)
            {
                return;
            }

            // Coalesce bursts of focus events (a click often fires several).
            Thread.Sleep(ProbeDebounceMs);
            long requested = Interlocked.Read(ref _probeRequestedTick);
            bool password = ProbeFocusedPasswordField();
            bool changed = password != _passwordFocus;
            _passwordFocus = password;
            Interlocked.Exchange(ref _probeCompletedTick, requested);

            if (changed)
            {
                Logger.Log($"InputContextGuard: password field focused={password}");
            }

            if (password)
            {
                PasswordFieldFocused?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static bool ProbeFocusedPasswordField()
    {
        // Classic Win32 edit with ES_PASSWORD: a cheap, exact answer.
        IntPtr focus = GetFocusedWindow();
        if (focus != IntPtr.Zero &&
            GetClassName(focus).Contains("edit", StringComparison.OrdinalIgnoreCase) &&
            ((long)NativeMethods.GetWindowLongPtr(focus, NativeMethods.GwlStyle) & NativeMethods.EsPassword) != 0)
        {
            return true;
        }

        // Browsers, Electron, WPF, UWP: ask UI Automation.
        try
        {
            AutomationElement? element = AutomationElement.FocusedElement;
            return element is not null && element.Current.IsPassword;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr GetFocusedWindow()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var info = new NativeMethods.GuiThreadInfo
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        return NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.HwndFocus != IntPtr.Zero
            ? info.HwndFocus
            : foreground;
    }
}

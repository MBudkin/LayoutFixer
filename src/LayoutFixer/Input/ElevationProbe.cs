using System;

namespace LayoutFixer.Input;

/// <summary>
/// Tells whether the foreground window belongs to a process LayoutFixer cannot
/// observe: an elevated ("Run as administrator") program while LayoutFixer
/// itself is not elevated, or the secure desktop (UAC prompt, lock screen).
/// Low-level hooks of a normal process do not see input sent to such windows,
/// so silence from the hooks is expected there.
/// </summary>
internal static class ElevationProbe
{
    private static readonly Lazy<bool> SelfElevated = new(() => IsProcessElevated((uint)Environment.ProcessId) ?? false);

    public static bool IsForegroundInaccessible()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0)
        {
            return true;
        }

        if (SelfElevated.Value)
        {
            return false;
        }

        // Unknown (access denied) is treated as inaccessible: protected and
        // elevated processes refuse the query.
        return IsProcessElevated(processId) ?? true;
    }

    private static bool? IsProcessElevated(uint processId)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out IntPtr token))
            {
                return null;
            }

            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevationClass, out int elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

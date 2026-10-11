using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LayoutFixer.Services;

internal sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LayoutFixer";

    public void Apply(bool enabled)
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (runKey is null)
        {
            throw new InvalidOperationException("Autostart registry key is unavailable.");
        }

        if (enabled)
        {
            runKey.SetValue(ValueName, Quote(Application.ExecutablePath), RegistryValueKind.String);
            return;
        }

        runKey.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }
}

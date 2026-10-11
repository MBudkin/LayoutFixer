using System;
using System.Threading;
using System.Windows.Forms;
using LayoutFixer.Diagnostics;
using LayoutFixer.Localization;
using LayoutFixer.Settings;
using LayoutFixer.UI;

namespace LayoutFixer;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\LayoutFixer.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);

        Logger.Log("==================================================");
        Logger.Log($"LayoutFixer starting, isFirstInstance={isFirstInstance}");

        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();
        Localizer.Apply(settings.Language);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                Localizer.Get("SecondInstanceMessage"),
                Localizer.Get("AppName"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, _) =>
        {
            DialogService.ShowError(null, "UnhandledUiError");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try
            {
                DialogService.ShowError(null, "UnhandledProcessError");
            }
            catch
            {
                // The process is already terminating; avoid writing logs or exposing input.
            }
        };

        using var context = new TrayApplicationContext(settingsService, settings);
        Application.Run(context);
    }
}

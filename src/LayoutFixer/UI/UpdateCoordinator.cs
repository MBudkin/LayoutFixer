using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using LayoutFixer.Diagnostics;
using LayoutFixer.Services;
using LayoutFixer.Settings;

namespace LayoutFixer.UI;

/// <summary>
/// Schedules the update check and remembers its outcome for the tray menu and
/// the settings window. Lives on the UI thread: the check itself is an async
/// HTTP request, so neither the keyboard hook nor the UI ever waits for it.
/// </summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const int StartupDelayMs = 60_000;
    private const int PollIntervalMs = 60 * 60_000;

    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Timer _timer = new();
    private Task<UpdateCheckResult>? _running;

    public UpdateCoordinator(AppSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _timer.Tick += (_, _) => HandleTick();
    }

    /// <summary>Raised on the UI thread whenever checking starts/stops or the result changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when an automatic check finds a release the user has not skipped.</summary>
    public event EventHandler<UpdateInfo>? UpdateFound;

    public bool IsChecking => _running is { IsCompleted: false };

    /// <summary>The newest release found by the last check (skipped or not); null when up to date.</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }

    /// <summary>The last check failed (no network, GitHub unavailable).</summary>
    public bool LastCheckFailed { get; private set; }

    /// <summary>An update exists and the user has not chosen to skip that version.</summary>
    public UpdateInfo? UnskippedUpdate =>
        AvailableUpdate is { } update && !IsSkipped(update) ? update : null;

    public void Start()
    {
        _timer.Interval = StartupDelayMs;
        _timer.Start();
    }

    /// <summary>Re-evaluates the schedule soon (e.g. right after the option was switched on).</summary>
    public void ScheduleSoon()
    {
        if (!_settings.CheckForUpdates || IsChecking)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = 5_000;
        _timer.Start();
    }

    public Task<UpdateCheckResult> CheckNowAsync()
    {
        return RunAsync(automatic: false);
    }

    public void Skip(UpdateInfo update)
    {
        _settings.SkippedUpdateVersion = update.Version.ToString(3);
        _saveSettings();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private bool IsSkipped(UpdateInfo update)
    {
        Version? skipped = UpdateChecker.ParseVersion(_settings.SkippedUpdateVersion);
        return skipped is not null && !UpdateChecker.IsNewer(update.Version, skipped);
    }

    private void HandleTick()
    {
        _timer.Interval = PollIntervalMs;
        if (!_settings.CheckForUpdates || IsChecking)
        {
            return;
        }

        DateTimeOffset? last = _settings.LastUpdateCheckUtc;
        if (last is { } value && DateTimeOffset.UtcNow - value < CheckInterval && value <= DateTimeOffset.UtcNow)
        {
            return;
        }

        _ = RunAsync(automatic: true);
    }

    private Task<UpdateCheckResult> RunAsync(bool automatic)
    {
        if (_running is { IsCompleted: false } running)
        {
            return running;
        }

        _running = RunCoreAsync(automatic);
        return _running;
    }

    // Started from the UI thread (timer tick or button), so the continuation
    // after the await runs on the UI thread again.
    private async Task<UpdateCheckResult> RunCoreAsync(bool automatic)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
        UpdateCheckResult result = await UpdateChecker.CheckAsync();
        Logger.Log($"Update check ({(automatic ? "auto" : "manual")}): {result.Status}" +
                   (result.Update is null ? string.Empty : $" {result.Update.Version}") +
                   (result.Error is null ? string.Empty : $" ({result.Error})"));

        LastCheckFailed = result.Status == UpdateCheckStatus.Failed;
        if (!LastCheckFailed)
        {
            AvailableUpdate = result.Update;
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _saveSettings();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        if (automatic && UnskippedUpdate is { } update)
        {
            UpdateFound?.Invoke(this, update);
        }

        return result;
    }
}

using System;
using System.Threading;
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
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _check;
    private Task<UpdateCheckResult>? _running;
    private bool _disposed;

    public UpdateCoordinator(AppSettings settings, Action saveSettings,
        Func<CancellationToken, Task<UpdateCheckResult>>? check = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _check = check ?? UpdateChecker.CheckAsync;
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
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
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
        if (_disposed)
        {
            return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Failed));
        }

        if (_running is { IsCompleted: false } running)
        {
            return running;
        }

        // Publish the task before raising StateChanged: subscribers must see
        // IsChecking=true and re-entrant clicks must share the same request.
        var completion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = completion.Task;
        _ = RunCoreAsync(automatic, completion);
        return completion.Task;
    }

    // Started from the UI thread (timer tick or button), so the continuation
    // after the await runs on the UI thread again.
    private async Task RunCoreAsync(bool automatic, TaskCompletionSource<UpdateCheckResult> completion)
    {
        UpdateCheckResult result;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            result = await _check(_lifetime.Token);
        }
        catch (Exception ex)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }

        _running = null;
        if (_disposed)
        {
            completion.TrySetResult(result);
            return;
        }

        try
        {
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
            if (automatic && _settings.CheckForUpdates && result.Status == UpdateCheckStatus.UpdateAvailable && UnskippedUpdate is { } update)
            {
                UpdateFound?.Invoke(this, update);
            }
        }
        finally
        {
            completion.TrySetResult(result);
        }
    }
}

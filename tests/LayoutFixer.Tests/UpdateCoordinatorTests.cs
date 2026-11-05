using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LayoutFixer.Services;
using LayoutFixer.Settings;
using LayoutFixer.UI;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task CheckingNotificationsReflectStateAndConcurrentChecksShareRequest()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>();
        int requests = 0;
        int saves = 0;
        var states = new List<bool>();
        var settings = new AppSettings();
        using var coordinator = new UpdateCoordinator(settings, () => saves++, _ =>
        {
            requests++;
            return response.Task;
        });
        Task<UpdateCheckResult>? reentrant = null;
        coordinator.StateChanged += (_, _) =>
        {
            states.Add(coordinator.IsChecking);
            if (coordinator.IsChecking) reentrant = coordinator.CheckNowAsync();
        };

        Task<UpdateCheckResult> first = coordinator.CheckNowAsync();
        Assert.Same(first, coordinator.CheckNowAsync());
        Assert.Same(first, reentrant);
        Assert.Equal(1, requests);
        Assert.Equal(new[] { true }, states);

        response.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
        await first;
        Assert.Equal(new[] { true, false }, states);
        Assert.False(coordinator.IsChecking);
        Assert.Equal(1, saves);
        Assert.NotNull(settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task SynchronousResponseDoesNotLeaveCheckingStateStuck()
    {
        var states = new List<bool>();
        using var coordinator = new UpdateCoordinator(new AppSettings(), () => { },
            _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate)));
        coordinator.StateChanged += (_, _) => states.Add(coordinator.IsChecking);
        await coordinator.CheckNowAsync();
        Assert.Equal(new[] { true, false }, states);
        Assert.False(coordinator.IsChecking);
    }

    [Fact]
    public async Task DisposalCancelsRequestAndPreventsLateNotificationsOrSettingsWrites()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>();
        CancellationToken token = default;
        int notifications = 0;
        int saves = 0;
        var coordinator = new UpdateCoordinator(new AppSettings(), () => saves++, cancellationToken =>
        {
            token = cancellationToken;
            return response.Task;
        });
        coordinator.StateChanged += (_, _) => notifications++;
        Task<UpdateCheckResult> check = coordinator.CheckNowAsync();
        coordinator.Dispose();
        Assert.True(token.IsCancellationRequested);
        response.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
        await check;
        Assert.Equal(1, notifications);
        Assert.Equal(0, saves);
        coordinator.Dispose();
    }

    [Fact]
    public async Task FailedAutomaticCheckDoesNotRepeatThePreviousUpdateNotification()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>();
        var settings = new AppSettings();
        var update = new UpdateInfo(new Version(99, 0, 0), "Update", "Notes", ProjectLinks.ReleasesUrl, null);
        int notifications = 0;
        using var coordinator = new UpdateCoordinator(settings, () => { }, _ => response.Task);
        coordinator.UpdateFound += (_, _) => notifications++;

        Tick(coordinator);
        Task<UpdateCheckResult> first = coordinator.CheckNowAsync();
        response.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update));
        await first;
        Assert.Equal(1, notifications);
        Assert.Same(update, coordinator.AvailableUpdate);

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-2);
        response = new TaskCompletionSource<UpdateCheckResult>();
        Tick(coordinator);
        Task<UpdateCheckResult> retry = coordinator.CheckNowAsync();
        response.SetResult(new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Offline"));
        await retry;

        Assert.True(coordinator.LastCheckFailed);
        Assert.Same(update, coordinator.AvailableUpdate);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task DisablingChecksDuringRequestPreventsAutomaticNotification()
    {
        var response = new TaskCompletionSource<UpdateCheckResult>();
        var settings = new AppSettings();
        var update = new UpdateInfo(new Version(99, 0, 0), "Update", "Notes", ProjectLinks.ReleasesUrl, null);
        int requests = 0;
        int notifications = 0;
        using var coordinator = new UpdateCoordinator(settings, () => { }, _ =>
        {
            requests++;
            return response.Task;
        });
        coordinator.UpdateFound += (_, _) => notifications++;

        Tick(coordinator);
        Task<UpdateCheckResult> running = coordinator.CheckNowAsync();
        settings.CheckForUpdates = false;
        response.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update));
        await running;

        Assert.Same(update, coordinator.AvailableUpdate);
        Assert.Equal(0, notifications);
        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-2);
        Tick(coordinator);
        Assert.Equal(1, requests);
    }

    private static void Tick(UpdateCoordinator coordinator)
    {
        // Exercise the timer callback without starting a WinForms message loop
        // or waiting for the real startup/hourly schedule.
        typeof(UpdateCoordinator).GetMethod("HandleTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, null);
    }
}

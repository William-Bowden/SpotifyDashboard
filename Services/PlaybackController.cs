using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services;

/// <summary>
/// Owns the dashboard's view of Spotify playback: device list, selected target device,
/// current playback state, background polling, and playback commands.
/// UI components read from it and subscribe to <see cref="StateChanged"/>.
/// </summary>
/// <remarks>
/// Safety rules enforced here:
/// <list type="bullet">
/// <item>Commands are only sent to the selected device, and only while it is the active device.</item>
/// <item>Moving playback to another device happens only through <see cref="TransferToSelectedAsync"/>,
/// which the UI calls only from an explicit button.</item>
/// <item>The selection merely defaults to whichever device is already active; it never picks a new target.</item>
/// </list>
/// </remarks>
public sealed class PlaybackController : IDisposable
{
    // Adaptive polling: frequent enough to feel live, light enough to stay well within rate limits.
    private static readonly TimeSpan PlayingPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PausedPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinPollDelay = TimeSpan.FromSeconds(1);

    // Poll shortly after the current track should end so the next track appears promptly.
    private static readonly TimeSpan TrackEndGrace = TimeSpan.FromMilliseconds(750);

    // Spotify's reported state lags slightly behind commands; wait before re-reading it.
    private static readonly TimeSpan SettleAfterCommand = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleAfterSkip = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SettleAfterTransfer = TimeSpan.FromMilliseconds(1200);

    private readonly SpotifyApiService _api;

    private CancellationTokenSource? _lifetimeCts;
    private TaskCompletionSource _wake = NewWakeSignal();
    private bool _pageVisible = true;

    // Stale-response protection: every command bumps _stateVersion so reads that started
    // before it are discarded; _readSeq discards reads that finish out of order.
    private long _stateVersion;
    private long _readSeq;
    private long _lastAppliedReadSeq;

    private DateTime _playbackSampledAtUtc;
    private DateTime _lastDeviceRefreshUtc;
    private DateTime _backoffUntilUtc;
    private DateTime _settleUntilUtc;
    private bool _deviceListStale;
    private bool _errorFromPoll;

    public PlaybackController(SpotifyApiService api)
    {
        _api = api;
    }

    /// <summary>Raised whenever any public state changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised when Spotify rejects the session (missing/expired/revoked token).
    /// Polling has already stopped and state has been cleared; the argument is a user-facing message.
    /// </summary>
    public event Action<string>? AuthenticationLost;

    public IReadOnlyList<SpotifyDevice>? Devices { get; private set; }
    public string? SelectedDeviceId { get; private set; }
    public SpotifyPlaybackState? Playback { get; private set; }
    public DateTime? DevicesUpdatedAt { get; private set; }
    public bool IsRefreshing { get; private set; }
    public bool IsCommandBusy { get; private set; }
    public string? Error { get; private set; }

    public bool IsRunning => _lifetimeCts is not null;

    public SpotifyDevice? SelectedDevice =>
        Devices?.FirstOrDefault(d => d.IsControllable && d.Id == SelectedDeviceId);

    /// <summary>The device Spotify is currently playing on, if any.</summary>
    public SpotifyDevice? ActiveDevice =>
        Playback?.Device ?? Devices?.FirstOrDefault(d => d.IsActive);

    /// <summary>
    /// A device is selected but it isn't the active one. Playback controls must stay
    /// hidden so a command can't implicitly move playback.
    /// </summary>
    public bool NeedsTransfer =>
        SelectedDevice is not null && SelectedDevice.Id != ActiveDevice?.Id;

    public bool CanControl =>
        SelectedDevice is not null && !NeedsTransfer && !IsCommandBusy && IsRunning;

    /// <summary>
    /// Current position estimated locally from the last sample, so the progress bar
    /// (and later, synced lyrics) can move smoothly between polls.
    /// </summary>
    public int EstimatedProgressMs
    {
        get
        {
            if (Playback is null)
                return 0;

            double progress = Playback.ProgressMs ?? 0;

            if (Playback.IsPlaying)
                progress += (DateTime.UtcNow - _playbackSampledAtUtc).TotalMilliseconds;

            var duration = Playback.Item?.DurationMs ?? 0;
            return duration > 0
                ? (int)Math.Clamp(progress, 0, duration)
                : (int)Math.Max(progress, 0);
        }
    }

    public bool IsDisallowed(string action) =>
        Playback?.Actions?.IsDisallowed(action) ?? false;

    // ---------- Lifecycle ----------

    /// <summary>Loads devices and playback state, then starts background polling.</summary>
    public async Task StartAsync()
    {
        if (_lifetimeCts is not null)
            return;

        var cts = new CancellationTokenSource();
        _lifetimeCts = cts;

        await RefreshAsync();

        // RefreshAsync may have stopped everything (e.g. authentication lost).
        if (ReferenceEquals(_lifetimeCts, cts))
            _ = PollLoopAsync(cts.Token);
    }

    /// <summary>Stops polling and cancels in-flight requests. State is kept.</summary>
    public void Stop()
    {
        var cts = _lifetimeCts;
        _lifetimeCts = null;
        cts?.Cancel();
        Wake();
    }

    /// <summary>Stops polling and clears all state (used on disconnect / auth loss).</summary>
    public void Reset()
    {
        Stop();

        Devices = null;
        SelectedDeviceId = null;
        Playback = null;
        DevicesUpdatedAt = null;
        Error = null;
        IsRefreshing = false;
        IsCommandBusy = false;
        _errorFromPoll = false;
        _deviceListStale = false;
        _backoffUntilUtc = default;
        _settleUntilUtc = default;

        NotifyChanged();
    }

    /// <summary>
    /// Called by the UI when the page becomes hidden/visible (screen off, tab switched).
    /// Polling pauses while hidden and refreshes immediately when visible again.
    /// </summary>
    public void SetPageVisible(bool visible)
    {
        if (_pageVisible == visible)
            return;

        _pageVisible = visible;

        if (visible)
            _deviceListStale = true;

        Wake();
    }

    public void SelectDevice(string? deviceId)
    {
        // Display-only: selecting never sends anything to Spotify.
        SelectedDeviceId = deviceId;
        NotifyChanged();
    }

    public void Dispose() => Stop();

    // ---------- Reading state ----------

    /// <summary>Reloads both the device list and the playback state.</summary>
    public async Task RefreshAsync()
    {
        if (IsRefreshing || LifetimeToken is not { IsCancellationRequested: false } ct)
            return;

        IsRefreshing = true;
        ClearError();
        NotifyChanged();

        try
        {
            await ReadStateAsync(includeDevices: true, ct);
        }
        catch (Exception ex)
        {
            HandleError(ex, fromPoll: false);
        }
        finally
        {
            IsRefreshing = false;
            NotifyChanged();
        }
    }

    private async Task ReadStateAsync(bool includeDevices, CancellationToken ct)
    {
        var version = _stateVersion;
        var seq = ++_readSeq;

        var devicesTask = includeDevices ? _api.GetDevicesAsync(ct) : null;
        var playbackTask = _api.GetPlaybackStateAsync(ct);

        if (devicesTask is not null)
            await Task.WhenAll(devicesTask, playbackTask);
        else
            await playbackTask;

        if (devicesTask is not null)
            ApplyDevices(devicesTask.Result);

        ApplyPlayback(playbackTask.Result, seq, version);

        _backoffUntilUtc = default;
        if (_errorFromPoll)
            ClearError();
    }

    private void ApplyDevices(List<SpotifyDevice> devices)
    {
        Devices = devices;
        DevicesUpdatedAt = DateTime.UtcNow;
        _lastDeviceRefreshUtc = DateTime.UtcNow;
        _deviceListStale = false;

        SyncDevicesFromPlayback();
        EnsureSelection();
    }

    private void ApplyPlayback(SpotifyPlaybackState? playback, long seq, long version)
    {
        // Discard reads that started before a command, or that finished out of order.
        if (version != _stateVersion || seq < _lastAppliedReadSeq)
            return;

        _lastAppliedReadSeq = seq;
        Playback = playback;
        _playbackSampledAtUtc = DateTime.UtcNow;

        // Playback moved to a device we haven't listed yet: refresh the list on the next poll.
        var activeId = playback?.Device?.Id;
        if (activeId is not null && Devices is not null && Devices.All(d => d.Id != activeId))
            _deviceListStale = true;

        SyncDevicesFromPlayback();
        EnsureSelection();
    }

    /// <summary>
    /// Keeps the device list's active flag and volume in step with the (more frequently
    /// refreshed) playback state, without an extra API call.
    /// </summary>
    private void SyncDevicesFromPlayback()
    {
        if (Devices is null)
            return;

        var playbackDevice = Playback?.Device;

        foreach (var device in Devices)
        {
            device.IsActive = playbackDevice?.Id is { } id && device.Id == id;

            if (device.IsActive && playbackDevice!.VolumePercent is int volume)
                device.VolumePercent = volume;
        }
    }

    /// <summary>
    /// If nothing valid is selected, highlight the device that is already active.
    /// Never selects a device that isn't already playing.
    /// </summary>
    private void EnsureSelection()
    {
        if (SelectedDevice is not null || Devices is null)
            return;

        var activeId = ActiveDevice?.Id;
        SelectedDeviceId = Devices.FirstOrDefault(d => d.IsControllable && d.Id == activeId)?.Id;
    }

    // ---------- Polling ----------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitForNextPollAsync(ComputeNextPollDelay(), ct);

                if (!_pageVisible || IsRefreshing || IsCommandBusy || DateTime.UtcNow < _settleUntilUtc)
                    continue;

                var includeDevices = _deviceListStale
                    || DateTime.UtcNow - _lastDeviceRefreshUtc >= DeviceRefreshInterval;

                try
                {
                    await ReadStateAsync(includeDevices, ct);
                }
                catch (Exception ex)
                {
                    HandleError(ex, fromPoll: true);
                }

                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private TimeSpan ComputeNextPollDelay()
    {
        if (!_pageVisible)
            return Timeout.InfiniteTimeSpan; // Woken by SetPageVisible(true).

        var now = DateTime.UtcNow;

        if (_settleUntilUtc > now)
            return _settleUntilUtc - now + TimeSpan.FromMilliseconds(50);

        if (_backoffUntilUtc > now)
            return _backoffUntilUtc - now;

        var playback = Playback;
        if (playback is null)
            return IdlePollInterval;

        if (!playback.IsPlaying)
            return PausedPollInterval;

        var duration = playback.Item?.DurationMs ?? 0;
        if (duration > 0)
        {
            var remaining = TimeSpan.FromMilliseconds(duration - EstimatedProgressMs) + TrackEndGrace;
            if (remaining < PlayingPollInterval)
                return remaining < MinPollDelay ? MinPollDelay : remaining;
        }

        return PlayingPollInterval;
    }

    /// <summary>Waits for the delay, or until <see cref="Wake"/> is called.</summary>
    private async Task WaitForNextPollAsync(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wake.Task;

        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await Task.WhenAny(wake, Task.Delay(delay, delayCts.Token));
            delayCts.Cancel();
        }

        if (wake.IsCompleted)
            _wake = NewWakeSignal();

        ct.ThrowIfCancellationRequested();
    }

    private void Wake() => _wake.TrySetResult();

    private static TaskCompletionSource NewWakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------- Commands ----------

    /// <summary>
    /// Explicitly moves playback to the selected device. If something is already playing
    /// (or paused) elsewhere, its play/pause state is kept. If nothing is playing anywhere,
    /// Spotify is asked to start playback on the selected device.
    /// </summary>
    public Task TransferToSelectedAsync()
    {
        var target = SelectedDevice;
        if (target?.Id is not { } deviceId || !NeedsTransfer)
            return Task.CompletedTask;

        var play = Playback?.IsPlaying ?? true;

        return ExecuteAsync(
            ct => _api.TransferPlaybackAsync(deviceId, play, ct),
            optimistic: null,
            SettleAfterTransfer,
            refreshDevices: true);
    }

    public Task TogglePlayPauseAsync()
    {
        var wasPlaying = Playback?.IsPlaying ?? false;

        return ExecuteOnSelectedAsync(
            (id, ct) => wasPlaying ? _api.PauseAsync(id, ct) : _api.PlayAsync(id, ct),
            optimistic: () =>
            {
                if (Playback is null)
                    return;

                FreezeProgress();
                Playback.IsPlaying = !wasPlaying;
            },
            SettleAfterCommand);
    }

    public Task NextAsync() =>
        ExecuteOnSelectedAsync((id, ct) => _api.NextAsync(id, ct), optimistic: null, SettleAfterSkip);

    public Task PreviousAsync() =>
        ExecuteOnSelectedAsync((id, ct) => _api.PreviousAsync(id, ct), optimistic: null, SettleAfterSkip);

    public Task SeekAsync(int positionMs) =>
        ExecuteOnSelectedAsync(
            (id, ct) => _api.SeekAsync(id, positionMs, ct),
            optimistic: () =>
            {
                if (Playback is null)
                    return;

                Playback.ProgressMs = positionMs;
                _playbackSampledAtUtc = DateTime.UtcNow;
            },
            SettleAfterCommand);

    public Task SetVolumeAsync(int volumePercent) =>
        ExecuteOnSelectedAsync(
            (id, ct) => _api.SetVolumeAsync(id, volumePercent, ct),
            optimistic: () =>
            {
                if (Playback?.Device is null)
                    return;

                Playback.Device.VolumePercent = volumePercent;
                SyncDevicesFromPlayback();
            },
            SettleAfterCommand);

    /// <summary>Runs a command against the selected device, only when it's the active device.</summary>
    private Task ExecuteOnSelectedAsync(
        Func<string, CancellationToken, Task> command,
        Action? optimistic,
        TimeSpan settle)
    {
        if (!CanControl || SelectedDevice?.Id is not { } deviceId)
            return Task.CompletedTask;

        return ExecuteAsync(ct => command(deviceId, ct), optimistic, settle, refreshDevices: false);
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task> command,
        Action? optimistic,
        TimeSpan settle,
        bool refreshDevices)
    {
        if (IsCommandBusy || LifetimeToken is not { IsCancellationRequested: false } ct)
            return;

        IsCommandBusy = true;
        ClearError();

        // Invalidate any read already in flight: it would carry pre-command state.
        _stateVersion++;
        _settleUntilUtc = DateTime.UtcNow + settle;

        optimistic?.Invoke();
        NotifyChanged();

        try
        {
            await command(ct);
        }
        catch (Exception ex)
        {
            HandleError(ex, fromPoll: false);
        }
        finally
        {
            IsCommandBusy = false;
            NotifyChanged();
        }

        if (!IsRunning)
            return;

        // Re-read what Spotify actually did; this also corrects a failed optimistic update.
        try
        {
            await Task.Delay(settle, ct);
            await ReadStateAsync(refreshDevices, ct);
        }
        catch (Exception ex)
        {
            HandleError(ex, fromPoll: true);
        }

        NotifyChanged();
    }

    /// <summary>Pins the estimated position before changing play/pause, so the bar doesn't jump.</summary>
    private void FreezeProgress()
    {
        if (Playback is null)
            return;

        Playback.ProgressMs = EstimatedProgressMs;
        _playbackSampledAtUtc = DateTime.UtcNow;
    }

    // ---------- Errors / helpers ----------

    private CancellationToken? LifetimeToken => _lifetimeCts?.Token;

    private void HandleError(Exception ex, bool fromPoll)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return;

            case SpotifyApiException { RequiresReauthentication: true } apiEx:
                Reset();
                AuthenticationLost?.Invoke(apiEx.Message);
                return;

            case SpotifyApiException { Kind: SpotifyApiErrorKind.RateLimited } apiEx:
                _backoffUntilUtc = DateTime.UtcNow + (apiEx.RetryAfter ?? ErrorBackoff);
                SetError(apiEx.Message, fromPoll);
                return;

            case SpotifyApiException { Kind: SpotifyApiErrorKind.Unavailable } apiEx when fromPoll:
                _backoffUntilUtc = DateTime.UtcNow + ErrorBackoff;
                SetError(apiEx.Message, fromPoll);
                return;

            default:
                SetError(ex.Message, fromPoll);
                return;
        }
    }

    private void SetError(string message, bool fromPoll)
    {
        // A background poll error must not replace a command error the user hasn't seen yet.
        if (fromPoll && Error is not null && !_errorFromPoll)
            return;

        Error = message;
        _errorFromPoll = fromPoll;
    }

    private void ClearError()
    {
        Error = null;
        _errorFromPoll = false;
    }

    private void NotifyChanged() => StateChanged?.Invoke();
}

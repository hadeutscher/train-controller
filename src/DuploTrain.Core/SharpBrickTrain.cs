using DuploTrain.Core.Config;
using Microsoft.Extensions.Logging;
using SharpBrick.PoweredUp;

namespace DuploTrain.Core;

/// <summary><see cref="ITrain"/> over SharpBrick's <see cref="DuploTrainBaseHub"/>.</summary>
public sealed class SharpBrickTrain : ITrain
{
    private readonly DuploTrainBaseHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<SharpBrickTrain> _logger;
    private readonly List<IDisposable> _subscriptions = [];

    private long _lastInboundAt;

    private SharpBrickTrain(DuploTrainBaseHub hub, TimeProvider time, ILogger<SharpBrickTrain> logger)
    {
        _hub = hub;
        _time = time;
        _logger = logger;
        _lastInboundAt = time.GetTimestamp();
    }

    public TimeSpan SinceLastInbound => _time.GetElapsedTime(Interlocked.Read(ref _lastInboundAt));

    public static async Task<SharpBrickTrain> CreateAsync(
        DuploTrainBaseHub hub,
        TimeProvider time,
        ILogger<SharpBrickTrain> logger,
        CancellationToken cancellationToken)
    {
        var train = new SharpBrickTrain(hub, time, logger);
        await train.SubscribeAsync(cancellationToken).ConfigureAwait(false);
        return train;
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        // Speed and battery are genuinely wanted. RSSI is subscribed purely so
        // PokeAsync has a reply to produce, giving the watchdog a heartbeat even
        // when the train is stationary and nothing else is being reported.
        await _hub.SetupHubPropertyNotificationAsync(HubProperty.Rssi, true).ConfigureAwait(false);
        await _hub.SetupHubPropertyNotificationAsync(HubProperty.BatteryVoltage, true).ConfigureAwait(false);
        await _hub.Speedometer
            .SetupNotificationAsync(_hub.Speedometer.ModeIndexSpeed, true, deltaInterval: 1)
            .ConfigureAwait(false);

        _subscriptions.Add(_hub.RssiObservable.Subscribe(_ => MarkInbound()));
        _subscriptions.Add(_hub.BatteryVoltageInPercentObservable.Subscribe(percent =>
        {
            MarkInbound();
            _logger.LogInformation("battery {Percent}%", percent);
        }));
        _subscriptions.Add(_hub.Speedometer.SpeedObservable.Subscribe(value =>
        {
            MarkInbound();
            _logger.LogDebug("measured speed {Speed}", value.SI);
        }));
        _subscriptions.Add(_hub.ButtonObservable.Subscribe(pressed =>
        {
            MarkInbound();
            if (pressed) _logger.LogInformation("train button pressed");
        }));

        _logger.LogInformation(
            "connected to {Name} (firmware {Firmware}, battery {Battery}%)",
            _hub.AdvertisingName, _hub.FirmwareVersion, _hub.BatteryVoltageInPercent);
    }

    private void MarkInbound() => Interlocked.Exchange(ref _lastInboundAt, _time.GetTimestamp());

    public Task SetPowerAsync(sbyte power, CancellationToken cancellationToken)
        => _hub.Motor.StartPowerAsync(power);

    public Task StopAsync(CancellationToken cancellationToken)
        => _hub.Motor.StopByBrakeAsync();

    public Task PlaySoundAsync(DuploTrainBaseSound sound, CancellationToken cancellationToken)
        => _hub.Speaker.PlaySoundAsync(sound);

    public Task SetColorAsync(PoweredUpColor color, CancellationToken cancellationToken)
        => _hub.RgbLight.SetRgbColorNoAsync(color);

    public Task PokeAsync(CancellationToken cancellationToken)
        => _hub.RequestHubPropertySingleUpdate(HubProperty.Rssi);

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();

        // Best effort: if the link is already gone these will throw, and that is
        // not worth surfacing during teardown.
        try { await _hub.Motor.StopByBrakeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "stop during dispose failed"); }

        try { await _hub.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "disconnect during dispose failed"); }

        _hub.Dispose();
    }
}

/// <summary>Discovers and connects a DUPLO train base via whichever bluetooth
/// adapter was registered — WinRT in production, the mock in tests.</summary>
public sealed class SharpBrickTrainConnector(
    PoweredUpHost host,
    TrainOptions options,
    TimeProvider time,
    ILoggerFactory loggerFactory) : ITrainConnector
{
    private readonly ILogger<SharpBrickTrainConnector> _logger =
        loggerFactory.CreateLogger<SharpBrickTrainConnector>();

    public async Task<ITrain> ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.DiscoverTimeoutSeconds));

        _logger.LogInformation(
            "scanning for a duplo train base for up to {Seconds}s - press the train's button so it blinks",
            options.DiscoverTimeoutSeconds);

        DuploTrainBaseHub hub;
        try
        {
            hub = await host.DiscoverAsync<DuploTrainBaseHub>(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"no train found within {options.DiscoverTimeoutSeconds}s - is it awake?");
        }

        await hub.ConnectAsync().ConfigureAwait(false);

        return await SharpBrickTrain
            .CreateAsync(hub, time, loggerFactory.CreateLogger<SharpBrickTrain>(), cancellationToken)
            .ConfigureAwait(false);
    }
}

using System.Threading.Channels;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpBrick.PoweredUp;

namespace DuploTrain.Core;

public sealed class TrainLinkLostException(TimeSpan silence)
    : Exception($"no traffic from the hub for {silence.TotalSeconds:F1}s");

/// <summary>Owns the connection, the arbiter and the input sources, and is the
/// only thing that talks to the train.
///
/// Input sources call into this as an <see cref="IInputSink"/> from their own
/// threads; nothing they do touches BLE directly. The drive loop is the single
/// place where a decision becomes a write.</summary>
public sealed class TrainRunner : BackgroundService, IInputSink
{
    private readonly ITrainConnector _connector;
    private readonly IReadOnlyList<IInputSource> _sources;
    private readonly DuploTrainOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TrainRunner> _logger;
    private readonly SpeedArbiter _arbiter;
    private readonly ColorCycle _colors;

    private readonly Channel<TrainAction> _actions =
        Channel.CreateBounded<TrainAction>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly object _gate = new();
    private double? _axis;
    private int _steps;

    public TrainRunner(
        ITrainConnector connector,
        IEnumerable<IInputSource> sources,
        IOptions<DuploTrainOptions> options,
        TimeProvider time,
        ILogger<TrainRunner> logger)
    {
        _connector = connector;
        _sources = sources.ToList();
        _options = options.Value;
        _options.Validate();
        _time = time;
        _logger = logger;
        _arbiter = new SpeedArbiter(_options.Motion, time);
        _colors = new ColorCycle(_options.Train.ColorCycle);

        if (_sources.Count == 0)
            throw new InvalidOperationException("no input sources registered; the train would be undrivable");
    }

    // --- IInputSink. Called from input threads; must not block. ---

    public void Throttle(double axis)
    {
        lock (_gate) _axis = axis;
    }

    public void Step(int direction)
    {
        lock (_gate) _steps += Math.Sign(direction);
    }

    public void Action(TrainAction action) => _actions.Writer.TryWrite(action);

    public void SourceLost(string source)
    {
        _logger.LogWarning("input source '{Source}' disappeared - stopping the train", source);

        // Clearing the axis matters: the source will send no further updates, so
        // its last value would otherwise persist as a live throttle request.
        lock (_gate)
        {
            _axis = 0;
            _steps = 0;
        }

        _actions.Writer.TryWrite(TrainAction.Stop);
    }

    // --- Drive loop ---

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartSources(stoppingToken);

        var backoff = TimeSpan.FromMilliseconds(_options.Train.ReconnectInitialMs);
        var maxBackoff = TimeSpan.FromMilliseconds(_options.Train.ReconnectMaxMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            ITrain? train = null;
            try
            {
                train = await _connector.ConnectAsync(stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(_options.Train.ReconnectInitialMs);

                // Never inherit a setpoint across a connection boundary.
                _arbiter.Clear();
                await DriveAsync(train, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TrainLinkLostException ex)
            {
                _logger.LogWarning("{Message} - the train may have gone to sleep; press its button", ex.Message);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning("{Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "train link failed");
            }
            finally
            {
                if (train is not null) await train.DisposeAsync().ConfigureAwait(false);
                _arbiter.Clear();
            }

            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("reconnecting in {Seconds:F1}s", backoff.TotalSeconds);
            try { await Task.Delay(backoff, _time, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var next = backoff * _options.Train.ReconnectFactor;
            backoff = next > maxBackoff ? maxBackoff : next;
        }

        _logger.LogInformation("stopped");
    }

    private void StartSources(CancellationToken stoppingToken)
    {
        foreach (var source in _sources)
        {
            _logger.LogInformation("starting input source '{Source}'", source.Name);
            _ = Task.Run(async () =>
            {
                try
                {
                    await source.RunAsync(this, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // expected on shutdown
                }
                catch (Exception ex)
                {
                    // A dead input source must not take the train with it, but the
                    // user has to know they have lost that control.
                    _logger.LogError(ex, "input source '{Source}' stopped", source.Name);
                    SourceLost(source.Name);
                }
            }, stoppingToken);
        }
    }

    private async Task DriveAsync(ITrain train, CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromMilliseconds(1000.0 / _options.Motion.PollHz);
        var heartbeat = TimeSpan.FromMilliseconds(_options.Motion.HeartbeatMs);
        var linkTimeout = TimeSpan.FromMilliseconds(_options.Motion.LinkTimeoutMs);
        var lastPoke = _time.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            ApplyPendingInput();
            await DispatchActionsAsync(train, cancellationToken).ConfigureAwait(false);

            if (_arbiter.TryTakeWrite(out var power))
            {
                _logger.LogDebug("power {Power}", power);
                await train.SetPowerAsync(power, cancellationToken).ConfigureAwait(false);
            }

            if (_options.Motion.HeartbeatMs > 0 && _time.GetElapsedTime(lastPoke) >= heartbeat)
            {
                await train.PokeAsync(cancellationToken).ConfigureAwait(false);
                lastPoke = _time.GetTimestamp();
            }

            if (_options.Motion.LinkTimeoutMs > 0 && train.SinceLastInbound > linkTimeout)
                throw new TrainLinkLostException(train.SinceLastInbound);

            await Task.Delay(poll, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyPendingInput()
    {
        double? axis;
        int steps;
        lock (_gate)
        {
            axis = _axis;
            _axis = null;
            steps = _steps;
            _steps = 0;
        }

        if (axis.HasValue) _arbiter.RequestAxis(axis.Value);
        for (var i = 0; i < Math.Abs(steps); i++) _arbiter.RequestStep(Math.Sign(steps));
    }

    private async Task DispatchActionsAsync(ITrain train, CancellationToken cancellationToken)
    {
        while (_actions.Reader.TryRead(out var action))
        {
            if (action == TrainAction.Stop)
            {
                _arbiter.RequestStop();
                await train.StopAsync(cancellationToken).ConfigureAwait(false);

                // The zero-power write is deliberately NOT consumed here. A
                // brake is power 127 - an actively held brake, not a state to
                // leave the motor in - so the next loop iteration writing zero
                // releases it a few milliseconds later. That makes the stop a
                // brake pulse followed by a coast, and it keeps the arbiter's
                // idea of the last written power equal to what the hub is
                // actually doing. Holding the brake instead locks the wheels
                // indefinitely and a later gentle throttle may not overcome it.
                _logger.LogInformation("stop");
                continue;
            }

            if (action is TrainAction.ThrottleUp or TrainAction.ThrottleDown)
            {
                _arbiter.RequestStep(action == TrainAction.ThrottleUp ? 1 : -1);
                continue;
            }

            if (action == TrainAction.CycleColor)
            {
                var color = _colors.Next();
                await train.SetColorAsync(color, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("led {Color}", color);
                continue;
            }

            if (ToSound(action) is { } sound)
            {
                await train.PlaySoundAsync(sound, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("sound {Sound}", sound);
            }
        }
    }

    private static DuploTrainBaseSound? ToSound(TrainAction action) => action switch
    {
        TrainAction.Horn => DuploTrainBaseSound.Horn,
        TrainAction.Brake => DuploTrainBaseSound.Brake,
        TrainAction.StationDeparture => DuploTrainBaseSound.StationDeparture,
        TrainAction.WaterRefill => DuploTrainBaseSound.WaterRefill,
        TrainAction.Steam => DuploTrainBaseSound.Steam,
        _ => null,
    };
}

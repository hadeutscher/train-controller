using SharpBrick.PoweredUp;

namespace DuploTrain.Core;

/// <summary>Everything the runner needs from a connected train.
///
/// Deliberately expressed in SharpBrick's own enums rather than wrapping them:
/// <c>SharpBrick.PoweredUp</c> targets netstandard2.1/net8.0 and is fully
/// portable, so re-declaring <c>DuploTrainBaseSound</c> or
/// <c>PoweredUpColor</c> would buy nothing but drift.</summary>
public interface ITrain : IAsyncDisposable
{
    /// <summary>Time since anything was heard from the hub. The watchdog uses
    /// this, because SharpBrick raises no event when a BLE link drops.</summary>
    TimeSpan SinceLastInbound { get; }

    Task SetPowerAsync(sbyte power, CancellationToken cancellationToken);

    /// <summary>Brake rather than coast — a stop should be decisive.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    Task PlaySoundAsync(DuploTrainBaseSound sound, CancellationToken cancellationToken);

    Task SetColorAsync(PoweredUpColor color, CancellationToken cancellationToken);

    /// <summary>Ask the hub for a property, purely so the watchdog has traffic
    /// to observe while the train is standing still.</summary>
    Task PokeAsync(CancellationToken cancellationToken);
}

/// <summary>Establishes a connection. Separate from <see cref="ITrain"/> so the
/// runner can reconnect by asking for a fresh instance rather than trying to
/// resurrect a dead one.</summary>
public interface ITrainConnector
{
    Task<ITrain> ConnectAsync(CancellationToken cancellationToken);
}

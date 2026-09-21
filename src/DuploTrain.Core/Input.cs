namespace DuploTrain.Core;

/// <summary>Where input sources send what they saw. Implementations must be safe
/// to call from any thread and must not block: sources run their own poll loops
/// and must never be held up by BLE latency.</summary>
public interface IInputSink
{
    /// <summary>Analog throttle in -1..1 (forward positive).</summary>
    void Throttle(double axis);

    /// <summary>Relative throttle change, for keys and scroll wheels.</summary>
    void Step(int direction);

    void Action(TrainAction action);

    /// <summary>The device vanished. The train stops: a controller that has been
    /// unplugged cannot be used to stop it.</summary>
    void SourceLost(string source);
}

public interface IInputSource
{
    string Name { get; }

    /// <summary>Runs until cancelled. Should log and keep going on transient
    /// device errors rather than tearing down the process.</summary>
    Task RunAsync(IInputSink sink, CancellationToken cancellationToken);
}

namespace DuploTrain.Core;

/// <summary>Somewhere to show what the train is doing. Implemented by the UI.
///
/// Kept deliberately tiny: the runner should not know whether anything is
/// listening, and a status display must never be able to affect control.
/// Implementations are called from the drive loop, so they must not block.</summary>
public interface IStatusSink
{
    void Connection(bool connected, string detail);

    void Power(int power);
}

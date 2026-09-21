namespace DuploTrain.Core;

/// <summary>A discrete thing an input source can ask the train to do. Throttle
/// is deliberately not in here: it is a continuous value, not an action.</summary>
public enum TrainAction
{
    Stop,
    Horn,
    Brake,
    StationDeparture,
    WaterRefill,
    Steam,
    CycleColor,
    ThrottleUp,
    ThrottleDown,
}

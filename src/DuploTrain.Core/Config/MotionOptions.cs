namespace DuploTrain.Core.Config;

/// <summary>How input is turned into motor power. All of it is configurable
/// because none of these numbers are knowable in advance for a given train,
/// track and child.</summary>
public sealed class MotionOptions
{
    /// <summary>Upper bound on motor power. The analog throttle is *scaled* into
    /// this range rather than clipped, so a full trigger pull always means
    /// "as fast as allowed" and the whole trigger travel stays useful.</summary>
    public int MaxSpeed { get; set; } = 60;

    /// <summary>Lowest power that actually moves the train.
    ///
    /// A DUPLO motor has a sizeable deadband: below roughly 30 it hums and the
    /// train crawls or does not move at all. Any non-zero request is therefore
    /// mapped into <see cref="MinPower"/>..<see cref="MaxSpeed"/> rather than
    /// 0..MaxSpeed, so the first notch and the start of the trigger travel both
    /// do something useful. Tune it per train and track; heavier loads and
    /// tired batteries need more. Set to 0 for the raw, unmapped range.</summary>
    public int MinPower { get; set; } = 30;

    /// <summary>Power added or removed per keyboard step.</summary>
    public int Step { get; set; } = 10;

    /// <summary>Fraction of trigger travel ignored near rest. Beyond it, power
    /// ramps from zero rather than jumping, so there is no step at the edge.</summary>
    public double Deadzone { get; set; } = 0.08;

    /// <summary>Cap on motor writes per second. The BLE link is the scarce
    /// resource; a 50 Hz input poll must not become 50 Hz of writes.</summary>
    public int RateLimitHz { get; set; } = 10;

    /// <summary>How often input devices are sampled.</summary>
    public int PollHz { get; set; } = 50;

    /// <summary>Stop the train if no inbound hub traffic arrives for this long.
    /// This is the only way to notice a dropped BLE link, because SharpBrick
    /// does not surface disconnects.</summary>
    public int LinkTimeoutMs { get; set; } = 6000;

    /// <summary>How often to poke the hub for a property so the watchdog above
    /// has something to observe even when the train is standing still.</summary>
    public int HeartbeatMs { get; set; } = 2000;

    public void Validate()
    {
        if (MaxSpeed is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxSpeed), MaxSpeed, "must be 1..100");
        if (MinPower is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MinPower), MinPower, "must be 0..100");
        if (MinPower > MaxSpeed)
            throw new ArgumentException(
                $"MinPower ({MinPower}) must not exceed MaxSpeed ({MaxSpeed})");
        if (Step is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Step), Step, "must be 1..100");
        if (Deadzone is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(Deadzone), Deadzone, "must be 0..<1");
        if (RateLimitHz is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(RateLimitHz), RateLimitHz, "must be 1..100");
        if (PollHz is < 1 or > 250)
            throw new ArgumentOutOfRangeException(nameof(PollHz), PollHz, "must be 1..250");
        if (HeartbeatMs > 0 && LinkTimeoutMs > 0 && LinkTimeoutMs <= HeartbeatMs)
            throw new ArgumentException(
                $"LinkTimeoutMs ({LinkTimeoutMs}) must exceed HeartbeatMs ({HeartbeatMs}), " +
                "or the watchdog fires before the heartbeat can answer it.");
    }
}

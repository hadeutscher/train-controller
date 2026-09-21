using DuploTrain.Core.Config;

namespace DuploTrain.Core;

/// <summary>Single owner of the requested motor power.
///
/// Input sources only ever <em>request</em>; this decides what actually gets
/// written and when. It is deliberately pure — no BLE, no timers, no threads —
/// so every rule in it (deadzone, cap, rate limit, change detection, stop
/// priority) is unit-testable without a train.
///
/// Not thread-safe: the runner owns one instance and drives it from one loop.
/// </summary>
public sealed class SpeedArbiter
{
    private readonly MotionOptions _options;
    private readonly TimeProvider _time;

    private int _requested;
    private int _lastWritten;
    private long _lastWriteAt;
    private bool _everWritten;

    public SpeedArbiter(MotionOptions options, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Power the train should be at, after deadzone and cap.</summary>
    public int Requested => _requested;

    private TimeSpan MinWriteInterval => TimeSpan.FromMilliseconds(1000.0 / _options.RateLimitHz);

    /// <summary>Request from an analog axis in -1..1 (right trigger minus left).</summary>
    public void RequestAxis(double axis) => _requested = Shape(axis);

    /// <summary>Request a relative change, for keyboard or scroll input.
    ///
    /// Steps land on powers that actually move the train: from rest the first
    /// step jumps straight to <see cref="MotionOptions.MinPower"/>, and stepping
    /// back below it stops rather than leaving the motor humming in its
    /// deadband.</summary>
    public void RequestStep(int direction)
    {
        var sign = Math.Sign(direction);
        if (sign == 0) return;

        var target = _requested + sign * _options.Step;
        var magnitude = Math.Abs(target);

        if (magnitude < _options.MinPower)
        {
            // Either leaving rest — snap up to the lowest power that moves — or
            // coming back down through the deadband, which means stop.
            _requested = _requested == 0 ? sign * _options.MinPower : 0;
            return;
        }

        _requested = Math.Sign(target) * Math.Min(magnitude, _options.MaxSpeed);
    }

    public void RequestStop() => _requested = 0;

    /// <summary>Forget everything after the link drops.
    ///
    /// Zeroing the setpoint is the point: without it a stale non-zero power
    /// would be considered "already written" and the train could lurch off as
    /// soon as it reconnected.
    ///
    /// Clearing the written-state matters just as much, and for a sharper
    /// reason: a DUPLO hub keeps executing its last motor command when BLE goes
    /// away, so a train that was moving when the link died is still moving. The
    /// first write after reconnecting must therefore be an explicit zero, not a
    /// write suppressed as "unchanged".</summary>
    public void Clear()
    {
        _requested = 0;
        _lastWritten = 0;
        _everWritten = false;
    }

    /// <summary>Decide whether a write is due, and consume it if so.
    ///
    /// Emits the next step of the ramp rather than the request itself, so a
    /// caller that keeps asking gets a sequence of powers that walks toward the
    /// target at the configured rate.</summary>
    public bool TryTakeWrite(out sbyte power)
    {
        var target = _requested;

        // Stopping is a safety action: immediate, never ramped. The rate limit
        // exists to protect the link from throttle chatter, not to delay the one
        // command that matters most.
        if (target == 0)
        {
            if (_everWritten && _lastWritten == 0)
            {
                power = 0;
                return false;
            }

            return Commit(0, out power);
        }

        if (_everWritten && _lastWritten == target)
        {
            power = (sbyte)_lastWritten;
            return false;
        }

        var elapsed = _everWritten ? _time.GetElapsedTime(_lastWriteAt) : MinWriteInterval;
        if (_everWritten && elapsed < MinWriteInterval)
        {
            power = (sbyte)_lastWritten;
            return false;
        }

        // Cap the ramp allowance at one write interval. Writes only happen while
        // the value is changing, so after holding a steady throttle the gap
        // since the last write can be seconds - and an uncapped allowance would
        // grant a jump straight to the target, silently defeating the ramp in
        // the most common case of all.
        var allowance = elapsed > MinWriteInterval ? MinWriteInterval : elapsed;

        return Commit(Slew(_everWritten ? _lastWritten : 0, target, allowance), out power);
    }

    private bool Commit(int value, out sbyte power)
    {
        _lastWritten = value;
        _lastWriteAt = _time.GetTimestamp();
        _everWritten = true;
        power = (sbyte)value;
        return true;
    }

    /// <summary>One step of the ramp from the current power toward the target.</summary>
    internal int Slew(int from, int to, TimeSpan elapsed)
    {
        // A reversal goes through rest rather than slamming from forward power
        // straight into reverse power.
        if (from != 0 && Math.Sign(to) != Math.Sign(from)) return 0;

        if (from == 0 && _options.MinPower > 0)
        {
            // Break away at the lowest power that actually moves. Ramping up
            // from zero would spend the first part of the ramp inside the
            // motor's deadband, humming, and then lurch.
            return Math.Abs(to) <= _options.MinPower
                ? to
                : Math.Sign(to) * _options.MinPower;
        }

        var rate = Math.Abs(to) > Math.Abs(from)
            ? _options.AccelerationPerSecond
            : _options.DecelerationPerSecond;

        if (rate <= 0) return to;

        // At least one unit per write, so a slow ramp still converges instead of
        // rounding to a standstill.
        var maxDelta = Math.Max(1, (int)Math.Round(rate * elapsed.TotalSeconds));
        var delta = to - from;

        return Math.Abs(delta) <= maxDelta ? to : from + Math.Sign(delta) * maxDelta;
    }

    /// <summary>Deadzone, then map the remaining travel onto the range of powers
    /// that actually drive the train.
    ///
    /// The mapping is MinPower..MaxSpeed rather than 0..MaxSpeed: the bottom of
    /// the trigger would otherwise sit inside the motor's deadband, so a third of
    /// the travel would do nothing. Just past the deadzone the train moves
    /// slowly; fully pulled it reaches the cap.</summary>
    internal int Shape(double axis)
    {
        if (double.IsNaN(axis)) return 0;

        axis = Math.Clamp(axis, -1.0, 1.0);
        var magnitude = Math.Abs(axis);
        if (magnitude <= _options.Deadzone) return 0;

        var beyond = (magnitude - _options.Deadzone) / (1.0 - _options.Deadzone);
        var span = _options.MaxSpeed - _options.MinPower;
        var power = _options.MinPower + (int)Math.Round(beyond * span);
        return axis < 0 ? -power : power;
    }
}

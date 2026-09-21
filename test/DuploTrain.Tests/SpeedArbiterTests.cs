using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DuploTrain.Tests;

public class SpeedArbiterTests
{
    // MinPower defaults to 0 here so the scaling, rate-limit and clear tests
    // exercise one rule at a time. The deadband mapping has its own tests below.
    // Ramping is off by default here so the scaling, rate-limit and clear tests
    // exercise one rule at a time. It has its own tests below.
    private static MotionOptions Options(int maxSpeed = 100, double deadzone = 0.1,
        int rateLimitHz = 10, int step = 10, int minPower = 0,
        int acceleration = 0, int deceleration = 0) => new()
    {
        MaxSpeed = maxSpeed,
        MinPower = minPower,
        Deadzone = deadzone,
        RateLimitHz = rateLimitHz,
        Step = step,
        AccelerationPerSecond = acceleration,
        DecelerationPerSecond = deceleration,
    };

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.05, 0)]
    [InlineData(0.1, 0)]
    [InlineData(-0.05, 0)]
    public void axis_inside_the_deadzone_is_zero(double axis, int expected)
    {
        var arbiter = new SpeedArbiter(Options());
        arbiter.RequestAxis(axis);
        Assert.Equal(expected, arbiter.Requested);
    }

    [Fact]
    public void power_ramps_from_zero_at_the_deadzone_edge_rather_than_jumping()
    {
        var arbiter = new SpeedArbiter(Options(deadzone: 0.1));

        arbiter.RequestAxis(0.1001);
        Assert.InRange(arbiter.Requested, 0, 1);

        arbiter.RequestAxis(0.55);
        Assert.Equal(50, arbiter.Requested);

        arbiter.RequestAxis(1.0);
        Assert.Equal(100, arbiter.Requested);
    }

    [Fact]
    public void full_trigger_reaches_exactly_the_cap_and_never_exceeds_it()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 30));

        arbiter.RequestAxis(1.0);
        Assert.Equal(30, arbiter.Requested);

        arbiter.RequestAxis(-1.0);
        Assert.Equal(-30, arbiter.Requested);
    }

    [Fact]
    public void the_cap_scales_the_axis_so_the_whole_trigger_travel_stays_useful()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 50, deadzone: 0));
        arbiter.RequestAxis(0.5);

        // Scaled, not clipped: half travel at a cap of 50 is 25, not 50.
        Assert.Equal(25, arbiter.Requested);
    }

    [Fact]
    public void steps_accumulate_and_clamp_to_the_cap()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 25, step: 10));

        arbiter.RequestStep(1);
        arbiter.RequestStep(1);
        Assert.Equal(20, arbiter.Requested);

        arbiter.RequestStep(1);
        Assert.Equal(25, arbiter.Requested);

        for (var i = 0; i < 10; i++) arbiter.RequestStep(-1);
        Assert.Equal(-25, arbiter.Requested);
    }

    [Fact]
    public void nan_is_treated_as_zero_rather_than_propagating()
    {
        var arbiter = new SpeedArbiter(Options());
        arbiter.RequestAxis(double.NaN);
        Assert.Equal(0, arbiter.Requested);
    }

    [Fact]
    public void an_unchanged_value_is_not_rewritten()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(Options(deadzone: 0), time);

        arbiter.RequestAxis(0.5);
        Assert.True(arbiter.TryTakeWrite(out var first));
        Assert.Equal(50, first);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(arbiter.TryTakeWrite(out _));
    }

    [Fact]
    public void changes_faster_than_the_rate_limit_are_suppressed_until_the_interval_passes()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(Options(deadzone: 0, rateLimitHz: 10), time);

        arbiter.RequestAxis(0.2);
        Assert.True(arbiter.TryTakeWrite(out _));

        time.Advance(TimeSpan.FromMilliseconds(50));
        arbiter.RequestAxis(0.3);
        Assert.False(arbiter.TryTakeWrite(out _));

        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(30, power);
    }

    [Fact]
    public void stopping_bypasses_the_rate_limit()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(Options(deadzone: 0, rateLimitHz: 10), time);

        arbiter.RequestAxis(0.8);
        Assert.True(arbiter.TryTakeWrite(out _));

        // Well inside the 100ms rate-limit window: a stop must still go out.
        time.Advance(TimeSpan.FromMilliseconds(5));
        arbiter.RequestStop();
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(0, power);
    }

    [Fact]
    public void clear_zeroes_the_setpoint_and_leaves_one_explicit_zero_write_pending()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(Options(deadzone: 0), time);

        arbiter.RequestAxis(0.9);
        Assert.True(arbiter.TryTakeWrite(out _));

        arbiter.Clear();
        Assert.Equal(0, arbiter.Requested);

        // A DUPLO hub keeps executing its last motor command when the BLE link
        // drops, so it is very likely still rolling. Commanding zero on the
        // fresh connection is what actually stops it; suppressing the write as
        // "unchanged" would leave it running.
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(0, power);

        // ...but only once.
        Assert.False(arbiter.TryTakeWrite(out _));
    }

    [Fact]
    public void after_clear_the_next_request_is_written_even_if_it_repeats_the_old_value()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(Options(deadzone: 0), time);

        arbiter.RequestAxis(0.4);
        Assert.True(arbiter.TryTakeWrite(out var before));

        arbiter.Clear();

        // Same value as before the clear. It must not be suppressed as
        // "unchanged", or the train would sit still after reconnecting while the
        // trigger is held.
        time.Advance(TimeSpan.FromSeconds(1));
        arbiter.RequestAxis(0.4);
        Assert.True(arbiter.TryTakeWrite(out var after));
        Assert.Equal(before, after);
    }

    // --- the motor deadband ---

    [Fact]
    public void just_past_the_deadzone_jumps_to_the_lowest_power_that_moves()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, deadzone: 0.1));

        arbiter.RequestAxis(0.1001);
        Assert.Equal(30, arbiter.Requested);

        arbiter.RequestAxis(-0.1001);
        Assert.Equal(-30, arbiter.Requested);
    }

    [Fact]
    public void trigger_travel_maps_across_the_whole_usable_power_range()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, deadzone: 0));

        arbiter.RequestAxis(0.5);
        Assert.Equal(45, arbiter.Requested);

        arbiter.RequestAxis(1.0);
        Assert.Equal(60, arbiter.Requested);
    }

    [Fact]
    public void the_deadzone_still_means_stopped_not_minimum_power()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, deadzone: 0.1));
        arbiter.RequestAxis(0.05);
        Assert.Equal(0, arbiter.Requested);
    }

    [Fact]
    public void the_first_step_from_rest_reaches_a_power_that_moves_the_train()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, step: 10));

        arbiter.RequestStep(1);
        Assert.Equal(30, arbiter.Requested);

        arbiter.RequestStep(1);
        Assert.Equal(40, arbiter.Requested);
    }

    [Fact]
    public void stepping_back_down_through_the_deadband_stops_rather_than_humming()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, step: 10));

        arbiter.RequestStep(1);
        Assert.Equal(30, arbiter.Requested);

        arbiter.RequestStep(-1);
        Assert.Equal(0, arbiter.Requested);
    }

    [Fact]
    public void stepping_down_from_rest_reverses_at_a_power_that_moves()
    {
        var arbiter = new SpeedArbiter(Options(maxSpeed: 60, minPower: 30, step: 10));
        arbiter.RequestStep(-1);
        Assert.Equal(-30, arbiter.Requested);
    }

    [Fact]
    public void min_power_above_the_cap_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => new SpeedArbiter(Options(maxSpeed: 20, minPower: 30)));
    }

    // --- acceleration ramp ---

    private static (SpeedArbiter Arbiter, FakeTimeProvider Time) Ramping()
    {
        var time = new FakeTimeProvider();
        var options = Options(maxSpeed: 60, minPower: 30, deadzone: 0,
            rateLimitHz: 10, acceleration: 60, deceleration: 120);
        return (new SpeedArbiter(options, time), time);
    }

    /// <summary>Drives the arbiter the way TrainRunner does and returns every
    /// power actually written.</summary>
    private static List<int> Writes(SpeedArbiter arbiter, FakeTimeProvider time, int ticks)
    {
        var written = new List<int>();
        for (var i = 0; i < ticks; i++)
        {
            if (arbiter.TryTakeWrite(out var power)) written.Add(power);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        return written;
    }

    [Fact]
    public void power_ramps_up_instead_of_jumping_to_the_request()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);

        var written = Writes(arbiter, time, 10);

        // Breaks away at MinPower, then climbs at 60/s = 6 per 100ms write.
        Assert.Equal(30, written[0]);
        Assert.Equal(36, written[1]);
        Assert.Equal(42, written[2]);
        Assert.Equal(60, written[^1]);
        Assert.True(written.Count > 4, "the ramp should take several writes");
    }

    [Fact]
    public void the_ramp_starts_at_the_breakaway_power_not_at_zero()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);

        var written = Writes(arbiter, time, 3);

        // Nothing below MinPower is ever written: those powers only make the
        // motor hum.
        Assert.All(written, p => Assert.True(Math.Abs(p) >= 30));
    }

    [Fact]
    public void the_ramp_settles_exactly_on_the_target_without_overshooting()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);

        var written = Writes(arbiter, time, 20);

        Assert.Equal(60, written[^1]);
        Assert.All(written, p => Assert.True(p <= 60));
    }

    [Fact]
    public void releasing_the_throttle_stops_immediately_rather_than_ramping_down()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);
        Writes(arbiter, time, 20);

        arbiter.RequestAxis(0.0);
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(0, power);
    }

    [Fact]
    public void a_stop_is_never_ramped_even_mid_acceleration()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);
        Writes(arbiter, time, 3);

        arbiter.RequestStop();
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(0, power);
    }

    [Fact]
    public void reversing_passes_through_rest_rather_than_slamming_into_reverse()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);
        Writes(arbiter, time, 20);

        arbiter.RequestAxis(-1.0);
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(0, power);
    }

    [Fact]
    public void deceleration_between_two_moving_powers_is_ramped()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);
        Writes(arbiter, time, 20);

        // Axis maps into MinPower..MaxSpeed, so 0.1 is 33, not 6. Coming down
        // from 60 at 120/s is 12 per 100ms write: 48, 36, then 33.
        arbiter.RequestAxis(0.1);

        Assert.True(arbiter.TryTakeWrite(out var first));
        Assert.Equal(48, first);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(arbiter.TryTakeWrite(out var second));
        Assert.Equal(36, second);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(arbiter.TryTakeWrite(out var third));
        Assert.Equal(33, third);
    }

    [Fact]
    public void a_long_steady_throttle_does_not_bank_up_a_ramp_allowance()
    {
        var (arbiter, time) = Ramping();
        arbiter.RequestAxis(1.0);
        Writes(arbiter, time, 20);

        // Nothing is written while the throttle sits at the target, so the gap
        // since the last write grows without bound. That must not translate into
        // permission to jump.
        time.Advance(TimeSpan.FromSeconds(30));

        arbiter.RequestAxis(0.1);
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(48, power);
    }

    [Fact]
    public void zero_acceleration_applies_power_immediately()
    {
        var time = new FakeTimeProvider();
        var arbiter = new SpeedArbiter(
            Options(maxSpeed: 60, minPower: 0, deadzone: 0, acceleration: 0), time);

        arbiter.RequestAxis(1.0);
        Assert.True(arbiter.TryTakeWrite(out var power));
        Assert.Equal(60, power);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void an_out_of_range_cap_is_rejected_at_construction(int maxSpeed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpeedArbiter(Options(maxSpeed: maxSpeed)));
    }
}

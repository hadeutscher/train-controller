using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DuploTrain.Tests;

public class SpeedArbiterTests
{
    private static MotionOptions Options(int maxSpeed = 100, double deadzone = 0.1,
        int rateLimitHz = 10, int step = 10) => new()
    {
        MaxSpeed = maxSpeed,
        Deadzone = deadzone,
        RateLimitHz = rateLimitHz,
        Step = step,
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

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void an_out_of_range_cap_is_rejected_at_construction(int maxSpeed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpeedArbiter(Options(maxSpeed: maxSpeed)));
    }
}

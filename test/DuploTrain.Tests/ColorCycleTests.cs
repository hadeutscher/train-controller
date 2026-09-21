using DuploTrain.Core;
using SharpBrick.PoweredUp;
using Xunit;

namespace DuploTrain.Tests;

public class ColorCycleTests
{
    [Fact]
    public void starts_unknown_because_the_hub_led_state_is_not_readable()
    {
        var cycle = new ColorCycle(["Red", "Green"]);
        Assert.Equal(PoweredUpColor.None, cycle.Current);
    }

    [Fact]
    public void advances_in_the_configured_order_and_wraps()
    {
        var cycle = new ColorCycle(["Red", "Green", "Black"]);

        Assert.Equal(PoweredUpColor.Red, cycle.Next());
        Assert.Equal(PoweredUpColor.Green, cycle.Next());
        Assert.Equal(PoweredUpColor.Black, cycle.Next());
        Assert.Equal(PoweredUpColor.Red, cycle.Next());
    }

    [Fact]
    public void current_tracks_the_last_advance()
    {
        var cycle = new ColorCycle(["Red", "Green"]);
        cycle.Next();
        Assert.Equal(PoweredUpColor.Red, cycle.Current);
        cycle.Next();
        Assert.Equal(PoweredUpColor.Green, cycle.Current);
    }

    [Fact]
    public void colour_names_are_case_insensitive()
    {
        var cycle = new ColorCycle(["lightblue"]);
        Assert.Equal(PoweredUpColor.LightBlue, cycle.Next());
    }

    [Fact]
    public void a_single_colour_is_allowed_and_simply_repeats()
    {
        var cycle = new ColorCycle(["White"]);
        Assert.Equal(PoweredUpColor.White, cycle.Next());
        Assert.Equal(PoweredUpColor.White, cycle.Next());
    }

    [Fact]
    public void an_unknown_colour_name_fails_loudly_and_lists_the_valid_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ColorCycle(["Chartreuse"]));
        Assert.Contains("Chartreuse", ex.Message);
        Assert.Contains(nameof(PoweredUpColor.LightBlue), ex.Message);
    }

    [Fact]
    public void none_is_rejected_because_it_is_a_sensor_reading_not_a_settable_colour()
    {
        Assert.Throws<ArgumentException>(() => new ColorCycle(["None"]));
    }

    [Fact]
    public void an_empty_cycle_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ColorCycle([]));
    }
}

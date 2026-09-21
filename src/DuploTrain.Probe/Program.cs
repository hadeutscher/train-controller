using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpBrick.PoweredUp;

// Milestone 0 connectivity probe. Proves, in order, that this PC can see the
// train, connect to it, read from it and write to it. The steps are ordered so
// a partial failure localises the problem: sounds need no motion, the LED needs
// no sensor, and the motor comes last.

const int DiscoverSeconds = 30;

var verbose = args.Contains("--trace");

var services = new ServiceCollection()
    .AddLogging(builder => builder
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
        .SetMinimumLevel(verbose ? LogLevel.Trace : LogLevel.Warning))
    .AddPoweredUp()
    .AddWinRTBluetooth()
    .BuildServiceProvider();

var host = services.GetRequiredService<PoweredUpHost>();

Step(1, $"scanning for a duplo train base ({DiscoverSeconds}s)");
Note("press the button on the train first, so its light blinks");

DuploTrainBaseHub train;
using (var discovery = new CancellationTokenSource(TimeSpan.FromSeconds(DiscoverSeconds)))
{
    try
    {
        train = await host.DiscoverAsync<DuploTrainBaseHub>(discovery.Token);
    }
    catch (OperationCanceledException)
    {
        return Fail(
            "no train found",
            "the train was asleep - press its button until it blinks, then retry",
            "windows has no working ble radio - check bluetooth in device manager",
            "this is a 2025 train (10427/10428) - it needs bonding and is not supported here",
            "rerun with --trace to see what the bluetooth stack reported");
    }
}

Ok("found a train base");

Step(2, "connecting");
await train.ConnectAsync();
Ok("connected");

try
{
    Step(3, "reading the battery (proves notifications flow inbound)");
    await train.Voltage.SetupNotificationAsync(train.Voltage.ModeIndexVoltageS, true, deltaInterval: 1);
    using var battery = train.Voltage.VoltageSObservable
        .Subscribe(v => Note($"battery {v.Pct}%  ({v.SI} mV)"));
    await Task.Delay(1500);

    Step(4, "playing the horn (proves a write reached the hub)");
    await train.Speaker.PlaySoundAsync(DuploTrainBaseSound.Horn);
    await Task.Delay(1200);
    Ok("if you heard a horn, the outbound path works");

    Step(5, "setting the led red, then green");
    await train.RgbLight.SetRgbColorNoAsync(PoweredUpColor.Red);
    await Task.Delay(900);
    await train.RgbLight.SetRgbColorNoAsync(PoweredUpColor.Green);
    Ok("led set");

    Step(6, "running the motor at 30% for 2s");
    await train.Speedometer.SetupNotificationAsync(train.Speedometer.ModeIndexSpeed, true, deltaInterval: 1);
    using var speed = train.Speedometer.SpeedObservable
        .Subscribe(v => Note($"speed {v.SI}"));

    await train.Speaker.PlaySoundAsync(DuploTrainBaseSound.StationDeparture);
    await train.Motor.StartPowerAsync(30);
    await Task.Delay(2000);
    await train.Motor.StopByFloatAsync();
    await Task.Delay(500);
    await train.Speaker.PlaySoundAsync(DuploTrainBaseSound.Brake);

    Console.WriteLine();
    Ok("all six steps passed - the windows ble path works end to end");
    return 0;
}
finally
{
    // The train must never be left running because the probe threw.
    try { await train.Motor.StopByFloatAsync(); } catch { /* link already gone */ }
}

static void Step(int n, string what) => Console.WriteLine($"{Environment.NewLine}[{n}/6] {what}");

static void Note(string what) => Console.WriteLine($"      {what}");

static void Ok(string what) => Console.WriteLine($"      ok - {what}");

static int Fail(string what, params string[] causes)
{
    Console.WriteLine($"{Environment.NewLine}FAILED: {what}");
    Console.WriteLine("likely causes, in order:");
    foreach (var cause in causes) Console.WriteLine($"  - {cause}");
    return 2;
}

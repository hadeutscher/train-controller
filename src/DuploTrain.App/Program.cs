using System.Diagnostics;
using DuploTrain.Core.Config;
using DuploTrain.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Content root must be the directory the dll lives in, not the working
// directory. The default is Directory.GetCurrentDirectory(), so running
// `dotnet C:\somewhere\duplo-train.dll` from a home directory would silently
// ignore the appsettings.json sitting next to the dll and use code defaults.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var section = builder.Configuration.GetSection(DuploTrainOptions.Section);
builder.Services.Configure<DuploTrainOptions>(section);

// Bind and validate once, up front. A bad binding or an out-of-range speed cap
// should fail on startup with a clear message, not on the first button press.
var options = section.Get<DuploTrainOptions>() ?? new DuploTrainOptions();
try
{
    options.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"configuration is invalid: {ex.Message}");
    return 1;
}

builder.Services.AddDuploTrainWindows(options.Input);

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var effective = host.Services.GetRequiredService<IOptions<DuploTrainOptions>>().Value;
logger.LogInformation(
    "max speed {MaxSpeed}, deadzone {Deadzone}, {RateLimit} writes/s, polling at {PollHz} Hz",
    effective.Motion.MaxSpeed, effective.Motion.Deadzone,
    effective.Motion.RateLimitHz, effective.Motion.PollHz);

await host.RunAsync();

// By here the host has stopped cleanly: TrainRunner has braked the motor and
// disconnected the hub. The process can still refuse to exit, because the
// WinRT bluetooth stack leaves threads behind that we neither own nor can join,
// and Ctrl+C then looks like a hang.
//
// Disposing the host first flushes the console logger's queue, so nothing is
// lost. After that there is genuinely nothing left to wait for, so terminate
// rather than blocking on somebody else's thread.
logger.LogDebug("exiting with {Threads} os threads alive",
    Process.GetCurrentProcess().Threads.Count);

host.Dispose();
Environment.Exit(0);
return 0;

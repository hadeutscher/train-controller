using DuploTrain.Core.Config;
using DuploTrain.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

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
return 0;

using DuploTrain.App;
using DuploTrain.Core;
using DuploTrain.Core.Config;
using DuploTrain.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must run before anything constructs a window - including the error
        // MessageBox below and the TrainWindow itself - or WinForms throws.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Content root must be the directory the dll lives in, not the working
        // directory. The default is Directory.GetCurrentDirectory(), so running
        // `dotnet C:\somewhere\duplo-train.dll` from a home directory would
        // silently ignore the appsettings.json sitting next to the dll.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        var section = builder.Configuration.GetSection(DuploTrainOptions.Section);
        builder.Services.Configure<DuploTrainOptions>(section);

        // Bind and validate up front. A bad binding or an out-of-range speed cap
        // should fail on startup with a clear message, not on the first press.
        var options = section.Get<DuploTrainOptions>() ?? new DuploTrainOptions();
        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"configuration is invalid:{Environment.NewLine}{ex.Message}",
                "duplo train", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // The default is 30s. Nothing here legitimately needs that long to stop:
        // when the train is connected the drive loop brakes and exits within a
        // poll interval, and when it is merely scanning there is no train to
        // brake. A long timeout only turns a stuck library call into an app that
        // appears not to close.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        builder.Services.AddDuploTrainWindows(options.Input, options.Session);

        // Constructed here rather than by the container, because the logging
        // provider needs the very same instance. Resolving it from a throwaway
        // provider would silently create a second window and log into the one
        // nobody can see.
        var window = new TrainWindow(Options.Create(options));

        builder.Services.AddSingleton(window);
        builder.Services.AddSingleton<IInputSource>(window);
        builder.Services.AddSingleton<IStatusSink>(window);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new WindowLoggerProvider(window));

        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<TrainWindow>>();
        logger.LogInformation(
            "max speed {MaxSpeed}, min power {MinPower}, deadzone {Deadzone}, {RateLimit} writes/s",
            options.Motion.MaxSpeed, options.Motion.MinPower,
            options.Motion.Deadzone, options.Motion.RateLimitHz);

        host.StartAsync().GetAwaiter().GetResult();

        Application.Run(window);

        // Closing the window stops the host, which brakes the motor and
        // disconnects the hub. Bounded, so that a library call which ignores its
        // cancellation token cannot keep the process alive after the user has
        // closed the window.
        try
        {
            if (!host.StopAsync(TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(6)))
                logger.LogWarning("shutdown did not complete in time - exiting anyway");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shutdown failed - exiting anyway");
        }

        // The WinRT bluetooth stack leaves threads behind that we neither own
        // nor can join, so the process would otherwise linger after the window
        // is gone. Everything has already been shut down cleanly by here.
        Environment.Exit(0);
        return 0;
    }
}

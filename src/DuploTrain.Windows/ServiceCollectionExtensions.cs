using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharpBrick.PoweredUp;

namespace DuploTrain.Windows;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Windows-only half: the WinRT bluetooth adapter and
    /// the physical input devices. Everything else lives in DuploTrain.Core so it
    /// stays testable off Windows.</summary>
    public static IServiceCollection AddDuploTrainWindows(
        this IServiceCollection services, InputOptions input)
    {
        services
            .AddPoweredUp()
            .AddWinRTBluetooth();

        services.AddSingleton(TimeProvider.System);

        // The connector needs just the train section, not the whole tree.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<DuploTrainOptions>>().Value.Train);
        services.AddSingleton<ITrainConnector, SharpBrickTrainConnector>();

        if (input.Gamepad.Enabled)
        {
            switch (input.Gamepad.Backend)
            {
                case GamepadBackend.WindowsGamingInput:
                    services.AddSingleton<IInputSource, GamepadInputSource>();
                    break;
                default:
                    services.AddSingleton<IInputSource, XInputGamepadSource>();
                    break;
            }
        }

        if (input.Keyboard.Enabled)
            services.AddSingleton<IInputSource, KeyboardInputSource>();

        services.AddHostedService<TrainRunner>();

        return services;
    }
}

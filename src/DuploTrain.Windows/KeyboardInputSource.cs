using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuploTrain.Windows;

/// <summary>Keyboard input via the console.
///
/// Uses <see cref="Console.ReadKey(bool)"/> rather than GetAsyncKeyState or a
/// low-level hook. That means keys only count while this console window has
/// focus — which is the unsurprising behaviour: a global hook would mean typing
/// "h" in another application honked the train.
///
/// The cost is that the throttle is stepped rather than analog, since the
/// console gives no key-up. That is fine; the gamepad is the analog path.</summary>
public sealed class KeyboardInputSource : IInputSource
{
    private readonly Dictionary<ConsoleKey, TrainAction> _bindings;
    private readonly ILogger<KeyboardInputSource> _logger;

    public KeyboardInputSource(IOptions<DuploTrainOptions> options, ILogger<KeyboardInputSource> logger)
    {
        _logger = logger;
        _bindings = Bind(options.Value.Input.Keyboard.Keys);
    }

    public string Name => "keyboard";

    private static Dictionary<ConsoleKey, TrainAction> Bind(Dictionary<string, TrainAction> configured)
    {
        var bindings = new Dictionary<ConsoleKey, TrainAction>();

        foreach (var (name, action) in configured)
        {
            if (!Enum.TryParse<ConsoleKey>(name, ignoreCase: true, out var key))
                throw new ArgumentException(
                    $"'{name}' is not a ConsoleKey. Examples: H, L, Spacebar, W, S, D.");

            if (key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                throw new ArgumentException(
                    $"'{name}' is reserved for the throttle steps and cannot be rebound.");

            bindings[key] = action;
        }

        return bindings;
    }

    public Task RunAsync(IInputSink sink, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            // Running as a service or with piped stdin. Not an error, but the
            // user should know why their keyboard does nothing.
            _logger.LogWarning("console input is redirected - keyboard control is unavailable");
            return Task.CompletedTask;
        }

        _logger.LogInformation("keyboard: up/down arrows throttle, {Count} bound keys (needs window focus)",
            _bindings.Count);

        // Console.ReadKey blocks, so this owns a thread of its own rather than
        // occupying a thread-pool slot for the life of the process.
        return Task.Factory.StartNew(
            () => Loop(sink, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void Loop(IInputSink sink, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                // Polling rather than a blocking read, so cancellation is
                // observed promptly instead of waiting for one more keypress.
                Thread.Sleep(15);
                continue;
            }

            var key = Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.UpArrow:
                    sink.Step(1);
                    break;
                case ConsoleKey.DownArrow:
                    sink.Step(-1);
                    break;
                default:
                    if (_bindings.TryGetValue(key, out var action)) sink.Action(action);
                    break;
            }
        }
    }
}

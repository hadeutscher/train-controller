using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Gaming.Input;

namespace DuploTrain.Windows;

/// <summary>Xbox controller input via <c>Windows.Gaming.Input</c>.
///
/// Chosen over an XInput P/Invoke because the WinRT projection is already
/// available — the TFM is mandated by SharpBrick.PoweredUp.WinRT anyway — and it
/// reports triggers as 0..1 doubles rather than raw bytes.
///
/// Device presence is polled rather than driven by the GamepadAdded/Removed
/// events: the same loop already runs at the poll rate, and polling a list is
/// less machinery than event subscriptions that want a dispatcher.</summary>
public sealed class GamepadInputSource : IInputSource
{
    private readonly MotionOptions _motion;
    private readonly Dictionary<GamepadButtons, TrainAction> _bindings;
    private readonly ILogger<GamepadInputSource> _logger;

    public GamepadInputSource(IOptions<DuploTrainOptions> options, ILogger<GamepadInputSource> logger)
    {
        var root = options.Value;
        _motion = root.Motion;
        _logger = logger;
        _bindings = Bind(root.Input.Gamepad.Buttons);
    }

    public string Name => "gamepad";

    private static Dictionary<GamepadButtons, TrainAction> Bind(Dictionary<string, TrainAction> configured)
    {
        var bindings = new Dictionary<GamepadButtons, TrainAction>();

        foreach (var (name, action) in configured)
        {
            if (!Enum.TryParse<GamepadButtons>(name, ignoreCase: true, out var button) ||
                button == GamepadButtons.None)
            {
                var valid = string.Join(", ", Enum.GetNames<GamepadButtons>()
                    .Where(n => n != nameof(GamepadButtons.None)));
                throw new ArgumentException(
                    $"'{name}' is not a gamepad button. Valid: {valid}");
            }

            bindings[button] = action;
        }

        return bindings;
    }

    public async Task RunAsync(IInputSink sink, CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromMilliseconds(1000.0 / _motion.PollHz);
        Gamepad? attached = null;
        var previousButtons = GamepadButtons.None;

        _logger.LogInformation(
            "gamepad: right trigger forward, left trigger reverse; {Count} bindings",
            _bindings.Count);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pads = Gamepad.Gamepads;
            var pad = pads.Count > 0 ? pads[0] : null;

            if (pad is null)
            {
                if (attached is not null)
                {
                    attached = null;
                    previousButtons = GamepadButtons.None;

                    // Deliberately routed through SourceLost rather than just
                    // logging: a controller that has been unplugged cannot be
                    // used to stop the train, so the train must stop itself.
                    sink.SourceLost(Name);
                }
            }
            else
            {
                if (!ReferenceEquals(pad, attached))
                {
                    _logger.LogInformation("gamepad connected");
                    attached = pad;
                    previousButtons = GamepadButtons.None;
                }

                GamepadReading reading;
                try
                {
                    reading = pad.GetCurrentReading();
                }
                catch (Exception ex)
                {
                    // Racing a disconnect. Next iteration will see it gone.
                    _logger.LogDebug(ex, "gamepad read failed");
                    await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                sink.Throttle(reading.RightTrigger - reading.LeftTrigger);

                // Rising edges only, so holding a button fires its action once.
                var justPressed = reading.Buttons & ~previousButtons;
                previousButtons = reading.Buttons;

                if (justPressed != GamepadButtons.None)
                {
                    foreach (var (button, action) in _bindings)
                        if ((justPressed & button) == button)
                            sink.Action(action);
                }
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }
}

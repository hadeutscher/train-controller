using System.Runtime.InteropServices;
using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuploTrain.Windows;

/// <summary>Xbox controller input via XInput.
///
/// This is the default backend because <c>Windows.Gaming.Input</c> enumerates
/// pads in a console app but returns empty readings: the WinRT input stack wants
/// a window and message pump that a console process does not have. XInput has no
/// such requirement — it is a plain export that works from any process.
///
/// Button names deliberately match <c>Windows.Gaming.Input</c>'s GamepadButtons
/// spelling (Menu/View rather than Start/Back) so appsettings.json stays valid
/// across both backends.</summary>
public sealed class XInputGamepadSource : IInputSource
{
    [Flags]
    internal enum PadButton : ushort
    {
        None = 0,
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        Menu = 0x0010,
        View = 0x0020,
        LeftThumbstick = 0x0040,
        RightThumbstick = 0x0080,
        LeftShoulder = 0x0100,
        RightShoulder = 0x0200,
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    private const uint Success = 0;
    private const uint DeviceNotConnected = 1167;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState14(uint userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState910(uint userIndex, out XInputState state);

    private readonly MotionOptions _motion;
    private readonly Dictionary<PadButton, TrainAction> _bindings;
    private readonly ILogger<XInputGamepadSource> _logger;
    private bool _useLegacyDll;

    public XInputGamepadSource(IOptions<DuploTrainOptions> options, ILogger<XInputGamepadSource> logger)
    {
        var root = options.Value;
        _motion = root.Motion;
        _logger = logger;
        _bindings = Bind(root.Input.Gamepad.Buttons);
    }

    public string Name => "gamepad (xinput)";

    private static Dictionary<PadButton, TrainAction> Bind(Dictionary<string, TrainAction> configured)
    {
        var bindings = new Dictionary<PadButton, TrainAction>();

        foreach (var (name, action) in configured)
        {
            if (!Enum.TryParse<PadButton>(name, ignoreCase: true, out var button) ||
                button == PadButton.None)
            {
                var valid = string.Join(", ", Enum.GetNames<PadButton>()
                    .Where(n => n != nameof(PadButton.None)));
                throw new ArgumentException($"'{name}' is not a gamepad button. Valid: {valid}");
            }

            bindings[button] = action;
        }

        return bindings;
    }

    private bool TryRead(out XInputState state)
    {
        if (!_useLegacyDll)
        {
            try
            {
                return GetState14(0, out state) == Success;
            }
            catch (DllNotFoundException)
            {
                // Pre-Windows 8 or a trimmed install. 9_1_0 ships with everything.
                _logger.LogInformation("xinput1_4.dll missing, falling back to xinput9_1_0.dll");
                _useLegacyDll = true;
            }
        }

        return GetState910(0, out state) == Success;
    }

    public Task RunAsync(IInputSink sink, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => Loop(sink, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void Loop(IInputSink sink, CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromMilliseconds(1000.0 / _motion.PollHz);
        var connected = false;
        var previous = PadButton.None;
        var lastAxis = 0.0;

        _logger.LogInformation(
            "gamepad: right trigger forward, left trigger reverse; {Count} bindings",
            _bindings.Count);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!TryRead(out var state))
            {
                if (connected)
                {
                    connected = false;
                    previous = PadButton.None;

                    // An unplugged pad cannot be used to stop the train, so the
                    // train stops itself.
                    sink.SourceLost(Name);
                }

                Thread.Sleep(poll);
                continue;
            }

            if (!connected)
            {
                connected = true;
                previous = PadButton.None;
                _logger.LogInformation("gamepad connected");
            }

            var axis = (state.Gamepad.RightTrigger - state.Gamepad.LeftTrigger) / 255.0;
            sink.Throttle(axis);

            if (Math.Abs(axis - lastAxis) > 0.01)
            {
                _logger.LogDebug("triggers lt={Left} rt={Right} axis={Axis:F2}",
                    state.Gamepad.LeftTrigger, state.Gamepad.RightTrigger, axis);
                lastAxis = axis;
            }

            var buttons = (PadButton)state.Gamepad.Buttons;
            var justPressed = buttons & ~previous;
            previous = buttons;

            if (justPressed != PadButton.None)
            {
                _logger.LogDebug("buttons pressed {Buttons}", justPressed);

                foreach (var (button, action) in _bindings)
                    if ((justPressed & button) == button)
                        sink.Action(action);
            }

            Thread.Sleep(poll);
        }
    }
}

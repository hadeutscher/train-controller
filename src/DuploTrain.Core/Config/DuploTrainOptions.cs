namespace DuploTrain.Core.Config;

/// <summary>Root of appsettings.json, bound under the "DuploTrain" section.</summary>
public sealed class DuploTrainOptions
{
    public const string Section = "DuploTrain";

    public TrainOptions Train { get; set; } = new();
    public MotionOptions Motion { get; set; } = new();
    public InputOptions Input { get; set; } = new();
    public SessionOptions Session { get; set; } = new();

    public void Validate()
    {
        Train.Validate();
        Motion.Validate();
        Input.Validate();
    }
}

public sealed class SessionOptions
{
    /// <summary>Stop Windows blanking the display or idling the machine while
    /// the app runs.
    ///
    /// The point is the lock screen: once locked, it receives the gamepad too
    /// and drives its on-screen keyboard with it, and no user-session process
    /// can take that away. Preventing the idle lock is the only lever there is.
    /// An explicit Win+L still locks.</summary>
    public bool KeepDisplayAwake { get; set; } = true;
}

public sealed class TrainOptions
{
    /// <summary>Discovery timeout for one connection attempt.</summary>
    public int DiscoverTimeoutSeconds { get; set; } = 30;

    /// <summary>First delay after a dropped link, before retrying.</summary>
    public int ReconnectInitialMs { get; set; } = 1000;

    /// <summary>Ceiling for the exponential reconnect backoff.</summary>
    public int ReconnectMaxMs { get; set; } = 30000;

    public double ReconnectFactor { get; set; } = 2.0;

    /// <summary>Colours the "cycle colour" action walks, in order. Defaults to a
    /// rainbow ending in Black, which is the LED's off state, so a child can
    /// reach "off" by pressing the same button rather than learning another.</summary>
    public string[] ColorCycle { get; set; } =
    [
        "Red", "Orange", "Yellow", "Green", "Cyan",
        "LightBlue", "Blue", "Purple", "Pink", "White", "Black"
    ];

    public void Validate()
    {
        if (DiscoverTimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(DiscoverTimeoutSeconds),
                DiscoverTimeoutSeconds, "must be 1..600");
        if (ReconnectInitialMs < 100)
            throw new ArgumentOutOfRangeException(nameof(ReconnectInitialMs),
                ReconnectInitialMs, "must be >= 100");
        if (ReconnectMaxMs < ReconnectInitialMs)
            throw new ArgumentException(
                $"ReconnectMaxMs ({ReconnectMaxMs}) must be >= ReconnectInitialMs ({ReconnectInitialMs})");
        if (ReconnectFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(ReconnectFactor),
                ReconnectFactor, "must be >= 1.0");
        if (ColorCycle.Length == 0)
            throw new ArgumentException("ColorCycle must list at least one colour");
    }
}

public sealed class InputOptions
{
    public GamepadOptions Gamepad { get; set; } = new();
    public KeyboardOptions Keyboard { get; set; } = new();

    public void Validate()
    {
        if (!Gamepad.Enabled && !Keyboard.Enabled)
            throw new ArgumentException("no input source is enabled; the train would be undrivable");
    }
}

public enum GamepadBackend
{
    /// <summary>Plain XInput exports. The default, because it works from a
    /// console process with no window or message pump.</summary>
    XInput,

    /// <summary><c>Windows.Gaming.Input</c>. Nicer API and 0..1 triggers, but in
    /// a console app it enumerates pads and then reports empty readings, because
    /// the WinRT input stack expects a window. Kept for a future windowed host.</summary>
    WindowsGamingInput,
}

public sealed class GamepadOptions
{
    public bool Enabled { get; set; } = true;

    public GamepadBackend Backend { get; set; } = GamepadBackend.XInput;

    /// <summary>Button name (a <c>GamepadButtons</c> flag) to action. Names are
    /// resolved by the Windows input layer, so this stays portable.</summary>
    public Dictionary<string, TrainAction> Buttons { get; set; } = new()
    {
        ["A"] = TrainAction.Horn,
        ["B"] = TrainAction.Stop,
        ["X"] = TrainAction.CycleColor,
        ["Y"] = TrainAction.WaterRefill,
        ["LeftShoulder"] = TrainAction.Steam,
        ["RightShoulder"] = TrainAction.StationDeparture,
    };
}

public sealed class KeyboardOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Key name (a <c>ConsoleKey</c>) to action. UpArrow and DownArrow
    /// are reserved for the throttle steps and ignored here.</summary>
    public Dictionary<string, TrainAction> Keys { get; set; } = new()
    {
        ["Spacebar"] = TrainAction.Stop,
        ["H"] = TrainAction.Horn,
        ["L"] = TrainAction.CycleColor,
        ["W"] = TrainAction.WaterRefill,
        ["S"] = TrainAction.Steam,
        ["D"] = TrainAction.StationDeparture,
    };
}

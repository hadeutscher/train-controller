using DuploTrain.Core;
using DuploTrain.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuploTrain.App;

/// <summary>The application window.
///
/// This exists for a control reason, not a cosmetic one. Windows routes gamepad
/// navigation at the focused window, and with only a console there was nowhere
/// safe for it to go — a stick nudge would walk the focus onto the console's
/// close button and a button press would shut the app down mid-run. A real
/// focusable window gives that navigation somewhere harmless to land.
///
/// It also owns keyboard input, replacing the console reader: a focused window
/// gets key events directly, so there is no need to poll the console or hook
/// keys globally.</summary>
public sealed class TrainWindow : Form, IInputSource, IStatusSink
{
    private readonly Dictionary<Keys, TrainAction> _bindings;
    private readonly Label _connection = new();
    private readonly Label _power = new();
    private readonly TextBox _log = new();
    private readonly object _logGate = new();

    private IInputSink? _sink;
    private TaskCompletionSource? _closed;

    private const int MaxLogCharacters = 40_000;

    public TrainWindow(IOptions<DuploTrainOptions> options)
    {
        _bindings = Bind(options.Value.Input.Keyboard.Keys);

        Text = "duplo train";
        Width = 720;
        Height = 480;
        MinimumSize = new Size(480, 320);
        KeyPreview = true;

        var status = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8),
        };

        _connection.AutoSize = true;
        _connection.Margin = new Padding(4, 10, 24, 4);
        _connection.Text = "starting";

        _power.AutoSize = true;
        _power.Margin = new Padding(4, 10, 24, 4);
        _power.Text = "power 0";

        var stop = new Button
        {
            Text = "STOP",
            Width = 120,
            Height = 30,
            // Never a default or focused button: the whole point of this window
            // is that stray gamepad "A" presses land somewhere harmless, and a
            // focused STOP would make them land on the one control that matters.
            TabStop = false,
        };
        stop.Click += (_, _) => _sink?.Action(TrainAction.Stop);

        status.Controls.Add(_connection);
        status.Controls.Add(_power);
        status.Controls.Add(stop);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.TabStop = false;
        _log.Font = new Font(FontFamily.GenericMonospace, 8.5f);

        Controls.Add(_log);
        Controls.Add(status);

        KeyDown += OnKeyDown;
        FormClosed += (_, _) => _closed?.TrySetResult();
    }

    // Explicit, because Control already has a Name and hiding it would be a
    // trap for anyone who later sets one in the designer.
    string IInputSource.Name => "window";

    private static Dictionary<Keys, TrainAction> Bind(Dictionary<string, TrainAction> configured)
    {
        var bindings = new Dictionary<Keys, TrainAction>();

        foreach (var (name, action) in configured)
        {
            if (!Enum.TryParse<Keys>(name, ignoreCase: true, out var key))
                throw new ArgumentException(
                    $"'{name}' is not a key name. Examples: H, L, Space, W, S, D.");

            if (key is Keys.Up or Keys.Down)
                throw new ArgumentException(
                    $"'{name}' is reserved for the throttle steps and cannot be rebound.");

            bindings[key] = action;
        }

        return bindings;
    }

    public Task RunAsync(IInputSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _closed.TrySetResult());
        return _closed.Task;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_sink is null) return;

        switch (e.KeyCode)
        {
            case Keys.Up:
                _sink.Step(1);
                break;
            case Keys.Down:
                _sink.Step(-1);
                break;
            default:
                if (!_bindings.TryGetValue(e.KeyCode, out var action)) return;
                _sink.Action(action);
                break;
        }

        // Swallow it so arrow keys do not also walk the focus around the window.
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    // --- IStatusSink. Called from the drive loop, never the UI thread. ---

    public void Connection(bool connected, string detail) =>
        OnUiThread(() => _connection.Text = connected ? $"train: {detail}" : $"train: {detail}...");

    public void Power(int power) => OnUiThread(() => _power.Text = $"power {power}");

    public void Append(string line)
    {
        lock (_logGate)
        {
            OnUiThread(() =>
            {
                if (_log.TextLength > MaxLogCharacters)
                    _log.Text = _log.Text[(MaxLogCharacters / 2)..];

                _log.AppendText(line + Environment.NewLine);
            });
        }
    }

    private void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException)
        {
            // Racing window close. Nothing to update.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between the check and the call.
        }
    }
}

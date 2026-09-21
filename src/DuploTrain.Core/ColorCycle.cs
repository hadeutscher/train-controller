using SharpBrick.PoweredUp;

namespace DuploTrain.Core;

/// <summary>Walks the configured LED colours in order, wrapping around.</summary>
public sealed class ColorCycle
{
    private readonly PoweredUpColor[] _colors;
    private int _index = -1;

    public ColorCycle(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _colors = names.Select(Parse).ToArray();
        if (_colors.Length == 0)
            throw new ArgumentException("at least one colour is required", nameof(names));
    }

    /// <summary>Current colour, or <see cref="PoweredUpColor.None"/> before the
    /// first advance — the hub's LED state is unknown until we set it.</summary>
    public PoweredUpColor Current => _index < 0 ? PoweredUpColor.None : _colors[_index];

    public PoweredUpColor Next()
    {
        _index = (_index + 1) % _colors.Length;
        return _colors[_index];
    }

    private static PoweredUpColor Parse(string name)
    {
        if (!Enum.TryParse<PoweredUpColor>(name, ignoreCase: true, out var color) ||
            color == PoweredUpColor.None)
        {
            var valid = string.Join(", ", Enum.GetNames<PoweredUpColor>()
                .Where(n => n != nameof(PoweredUpColor.None)));
            throw new ArgumentException($"'{name}' is not a settable LED colour. Valid: {valid}");
        }

        return color;
    }
}

namespace MouseBat;

public enum ReadStatus
{
    /// <summary>No Logitech receiver or wired mouse is plugged in.</summary>
    NoReceiver,
    /// <summary>The receiver is there, but the mouse didn't answer (asleep or switched off).</summary>
    MouseUnreachable,
    Ok,
}

public sealed record BatteryReading(ReadStatus Status, int Millivolts = 0, bool Charging = false);

public static class BatteryCurve
{
    // The G502 Lightspeed reports only a voltage, so the percentage is an estimate.
    // Discharge curve for the Logitech G LiPo cells, as used by the Solaar project.
    private static readonly (int Millivolts, int Percent)[] Points =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];

    public static int ToPercent(int millivolts)
    {
        if (millivolts >= Points[0].Millivolts) return 100;
        if (millivolts <= Points[^1].Millivolts) return 0;

        for (int i = 1; i < Points.Length; i++)
        {
            var (loMv, loPct) = Points[i];
            if (millivolts < loMv) continue;

            var (hiMv, hiPct) = Points[i - 1];
            double t = (double)(millivolts - loMv) / (hiMv - loMv);
            return (int)Math.Round(loPct + t * (hiPct - loPct));
        }
        return 0;
    }
}

/// <summary>
/// Battery voltage sags while the mouse is working and recovers when idle. A median of
/// recent readings stops the percentage jittering and one sag from raising a false alert.
/// </summary>
public sealed class VoltageSmoother(int window = 5)
{
    private readonly Queue<int> _samples = new();
    private bool _lastCharging;

    public int Add(int millivolts, bool charging)
    {
        // Voltage jumps when charging starts or stops; old samples no longer apply.
        if (charging != _lastCharging) _samples.Clear();
        _lastCharging = charging;

        _samples.Enqueue(millivolts);
        if (_samples.Count > window) _samples.Dequeue();

        var sorted = _samples.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}

public enum AlertLevel { None, Low, Critical }

/// <summary>
/// Decides when to notify. Each level fires once per discharge, and re-arms only after
/// charging or a clear recovery, so a reading hovering at the threshold can't spam.
/// </summary>
public sealed class LowBatteryAlert(int lowThreshold)
{
    private const int RearmMargin = 5;

    private int _low = lowThreshold;
    private bool _lowSent;
    private bool _criticalSent;

    private int Critical => _low / 2;

    public void SetThreshold(int lowThreshold)
    {
        _low = lowThreshold;
        _lowSent = _criticalSent = false;
    }

    public AlertLevel Evaluate(int percent, bool charging)
    {
        if (charging)
        {
            _lowSent = _criticalSent = false;
            return AlertLevel.None;
        }

        if (percent >= _low + RearmMargin) _lowSent = false;
        if (percent >= Critical + RearmMargin) _criticalSent = false;

        if (percent <= Critical && !_criticalSent)
        {
            _lowSent = _criticalSent = true;
            return AlertLevel.Critical;
        }
        if (percent <= _low && !_lowSent)
        {
            _lowSent = true;
            return AlertLevel.Low;
        }
        return AlertLevel.None;
    }
}

namespace MouseBat;

public enum ReadStatus
{
    /// <summary>No Logitech receiver or wired mouse is plugged in.</summary>
    NoReceiver,
    /// <summary>The receiver is there, but the mouse didn't answer (asleep or switched off).</summary>
    MouseUnreachable,
    Ok,
}

public sealed record BatteryReading(
    ReadStatus Status,
    string? DeviceName = null,
    int? Percent = null,
    int? Millivolts = null,
    bool Charging = false);

/// <summary>A decoded battery report. Millivolts is set only for mice that report a voltage.</summary>
public sealed record BatterySample(int Percent, bool Charging, int? Millivolts = null);

/// <summary>Decodes the HID++ 2.0 battery features. Each returns null if the report is unusable.</summary>
public static class BatteryDecoder
{
    // Status byte shared by features 0x1000 and 0x1004.
    private const byte Recharging = 1, AlmostFull = 2, Full = 3, SlowRecharge = 4;

    /// <summary>Feature 0x1000: [level %, next level %, status].</summary>
    public static BatterySample? BatteryStatus(byte[] report)
    {
        if (report.Length < 3) return null;

        byte status = report[2];
        int? percent = report[0] != 0 ? report[0] : status switch
        {
            // No level reported, so infer a rough one from the charge state.
            Full => 90,
            Recharging or AlmostFull => 50,
            SlowRecharge => 20,
            _ => null,
        };
        return percent is { } value ? new BatterySample(value, IsCharging(status)) : null;
    }

    /// <summary>Feature 0x1001: [millivolts (big-endian, 2 bytes), flags]. Bit 7 means charging.</summary>
    public static BatterySample? BatteryVoltage(byte[] report)
    {
        if (report.Length < 3) return null;
        return FromVoltage((report[0] << 8) | report[1], (report[2] & 0x80) != 0);
    }

    /// <summary>Feature 0x1004: [state of charge %, level flag, status]. Some mice report only the flag.</summary>
    public static BatterySample? UnifiedBattery(byte[] report)
    {
        if (report.Length < 3) return null;

        int percent = report[0] != 0 ? report[0] : report[1] switch
        {
            8 => 90,
            4 => 50,
            2 => 20,
            1 => 5,
            _ => 0,
        };
        return new BatterySample(percent, IsCharging(report[2]));
    }

    /// <summary>Feature 0x1F20: [millivolts (big-endian, 2 bytes), flags]. Bit 0 means valid, bit 1 charging.</summary>
    public static BatterySample? AdcMeasurement(byte[] report)
    {
        if (report.Length < 3 || (report[2] & 0x01) == 0) return null;
        return FromVoltage((report[0] << 8) | report[1], (report[2] & 0x02) != 0);
    }

    private static BatterySample? FromVoltage(int millivolts, bool charging) =>
        millivolts == 0 ? null : new BatterySample(BatteryCurve.ToPercent(millivolts), charging, millivolts);

    private static bool IsCharging(byte status) => status is Recharging or AlmostFull or Full or SlowRecharge;
}

public static class BatteryCurve
{
    // Mice that report only a voltage get an estimated percentage from this discharge curve,
    // taken from the Solaar project. It's a single curve, so accuracy varies by model.
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

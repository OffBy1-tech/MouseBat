using System.Text;

namespace MouseBat;

public interface IHidppChannel
{
    /// <summary>Returns the response parameters, or null on an error reply or timeout.</summary>
    Task<byte[]?> CallAsync(byte device, byte feature, byte function, params byte[] parameters);
}

/// <summary>Finds a mouse on a HID++ 2.0 channel and reads its battery.</summary>
public static class HidppMouse
{
    private const ushort FeatureDeviceName = 0x0005;
    private const byte KindMouse = 0x03, KindTrackball = 0x05;

    // 1-6 are receiver pairing slots (7 is the POWERPLAY mat, which has no battery).
    // 0xFF addresses a device directly, for when the mouse is plugged in by cable.
    private static readonly byte[] DeviceSlots = [1, 2, 3, 4, 5, 6, 0xFF];

    // Mice implement one of these, depending on age and model. Same order as Solaar.
    private static readonly (ushort Id, byte Function, Func<byte[], BatterySample?> Decode)[] BatteryFeatures =
    [
        (0x1000, 0, BatteryDecoder.BatteryStatus),
        (0x1001, 0, BatteryDecoder.BatteryVoltage),
        (0x1004, 1, BatteryDecoder.UnifiedBattery),
        (0x1F20, 0, BatteryDecoder.AdcMeasurement),
    ];

    /// <summary>Returns the first mouse that reports a battery, or null if none does.</summary>
    public static async Task<BatteryReading?> ReadFirstAsync(IHidppChannel channel)
    {
        foreach (byte slot in DeviceSlots)
        {
            if (await TryReadSlotAsync(channel, slot) is { } reading) return reading;
        }
        return null;
    }

    private static async Task<BatteryReading?> TryReadSlotAsync(IHidppChannel channel, byte slot)
    {
        if (await FindFeatureAsync(channel, slot, FeatureDeviceName) is not { } nameFeature) return null;

        // Keyboards and headsets report batteries too; this app is only about mice.
        var kind = await channel.CallAsync(slot, nameFeature, function: 2);
        if (kind is not [KindMouse or KindTrackball, ..]) return null;

        if (await ReadBatteryAsync(channel, slot) is not { } sample) return null;

        var name = await ReadNameAsync(channel, slot, nameFeature);
        return new BatteryReading(ReadStatus.Ok, name, sample.Percent, sample.Millivolts, sample.Charging);
    }

    private static async Task<BatterySample?> ReadBatteryAsync(IHidppChannel channel, byte slot)
    {
        foreach (var (id, function, decode) in BatteryFeatures)
        {
            if (await FindFeatureAsync(channel, slot, id) is not { } feature) continue;

            var response = await channel.CallAsync(slot, feature, function);
            if (response != null && decode(response) is { } sample) return sample;
        }
        return null;
    }

    private static async Task<byte?> FindFeatureAsync(IHidppChannel channel, byte slot, ushort featureId)
    {
        // The Root feature is always index 0; function 0 maps a feature ID to its index.
        var result = await channel.CallAsync(slot, 0x00, 0, (byte)(featureId >> 8), (byte)featureId);
        return result is [> 0, ..] ? result[0] : null;
    }

    private static async Task<string?> ReadNameAsync(IHidppChannel channel, byte slot, byte feature)
    {
        var length = await channel.CallAsync(slot, feature, function: 0);
        if (length is not [> 0, ..]) return null;

        var name = new StringBuilder();
        while (name.Length < length[0])
        {
            var chunk = await channel.CallAsync(slot, feature, 1, (byte)name.Length);
            if (chunk is not { Length: > 0 }) return null;
            name.Append(Encoding.ASCII.GetString(chunk, 0, Math.Min(chunk.Length, length[0] - name.Length)));
        }
        return name.ToString();
    }
}

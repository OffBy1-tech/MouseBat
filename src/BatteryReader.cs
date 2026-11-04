using System.Text;
using System.Text.RegularExpressions;

namespace MouseBat;

/// <summary>
/// Reads the battery over HID++ 2.0, Logitech's vendor protocol. Handles are opened for
/// each read and shared, so this survives replugging and coexists with G HUB.
/// </summary>
public static partial class BatteryReader
{
    private const ushort LogitechVendorId = 0x046D;
    private const ushort HidppUsagePage = 0xFF00, ShortUsage = 0x0001, LongUsage = 0x0002;

    private const ushort FeatureDeviceName = 0x0005, FeatureBatteryVoltage = 0x1001;
    private const string ExpectedNameFragment = "G502";

    // 1-6 are receiver pairing slots (7 is the POWERPLAY mat, which has no battery).
    // 0xFF addresses a device directly, for when the mouse is plugged in by cable.
    private static readonly byte[] DeviceSlots = [1, 2, 3, 4, 5, 6, 0xFF];

    public static async Task<BatteryReading> ReadAsync()
    {
        var receivers = FindHidppDevices();
        if (receivers.Count == 0) return new BatteryReading(ReadStatus.NoReceiver);

        foreach (var (longInterface, shortInterface) in receivers)
        {
            try
            {
                using var channel = new HidppChannel(longInterface, shortInterface);
                foreach (byte slot in DeviceSlots)
                {
                    if (await TryReadSlotAsync(channel, slot) is { } reading) return reading;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unplugged mid-read, or unavailable. Move on; the next poll tries again.
            }
        }
        return new BatteryReading(ReadStatus.MouseUnreachable);
    }

    private static async Task<BatteryReading?> TryReadSlotAsync(HidppChannel channel, byte slot)
    {
        if (await FindFeatureAsync(channel, slot, FeatureBatteryVoltage) is not { } batteryFeature) return null;

        // Other Logitech devices can report voltage too, so confirm this one is the mouse.
        var name = await ReadDeviceNameAsync(channel, slot);
        if (name == null || !name.Contains(ExpectedNameFragment, StringComparison.OrdinalIgnoreCase)) return null;

        var battery = await channel.CallAsync(slot, batteryFeature, function: 0);
        if (battery == null) return null;

        int millivolts = (battery[0] << 8) | battery[1];
        bool charging = (battery[2] & 0x80) != 0;
        return new BatteryReading(ReadStatus.Ok, millivolts, charging);
    }

    private static async Task<byte?> FindFeatureAsync(HidppChannel channel, byte slot, ushort featureId)
    {
        // The Root feature is always index 0; function 0 maps a feature ID to its index.
        var result = await channel.CallAsync(slot, 0x00, function: 0, (byte)(featureId >> 8), (byte)featureId);
        return result == null || result[0] == 0 ? null : result[0];
    }

    private static async Task<string?> ReadDeviceNameAsync(HidppChannel channel, byte slot)
    {
        if (await FindFeatureAsync(channel, slot, FeatureDeviceName) is not { } feature) return null;

        var length = await channel.CallAsync(slot, feature, function: 0);
        if (length == null) return null;

        var name = new StringBuilder();
        while (name.Length < length[0])
        {
            var chunk = await channel.CallAsync(slot, feature, function: 1, (byte)name.Length);
            if (chunk == null) return null;
            name.Append(Encoding.ASCII.GetString(chunk, 0, Math.Min(chunk.Length, length[0] - name.Length)));
        }
        return name.ToString();
    }

    private static List<(HidInterface Long, HidInterface? Short)> FindHidppDevices()
    {
        var hidpp = HidNative.Enumerate(LogitechVendorId).Where(d => d.UsagePage == HidppUsagePage).ToList();

        return hidpp
            .Where(d => d.Usage == LongUsage)
            .Select(l => (l, hidpp.FirstOrDefault(s => s.Usage == ShortUsage && SameDevice(s, l))))
            .ToList();
    }

    private static bool SameDevice(HidInterface a, HidInterface b) =>
        CollectionSuffix().Replace(a.Path, "") == CollectionSuffix().Replace(b.Path, "");

    // Windows exposes each top-level collection as its own path, differing only in these parts:
    //   hid#vid_046d&pid_c53a&mi_02&col01#e&132597c3&0&0000#{guid}
    [GeneratedRegex("&col[0-9a-f]+|&[0-9a-f]{4}#", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionSuffix();
}

/// <summary>
/// One request/response at a time over a HID++ device. Requests go out as long reports, but
/// replies can come back on either collection: receivers send "no such device" errors on the
/// short one, so both must be read.
/// </summary>
internal sealed class HidppChannel : IDisposable
{
    private const byte LongReportId = 0x11;
    private const byte Hidpp10Error = 0x8F, Hidpp20Error = 0xFF;
    private const byte SoftwareId = 0x0A; // Echoed in replies; distinguishes ours from G HUB's.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    private readonly FileStream _long;
    private readonly FileStream? _short;
    private readonly int _longOutputLength, _longInputLength, _shortInputLength;

    public HidppChannel(HidInterface longInterface, HidInterface? shortInterface)
    {
        _long = HidNative.OpenAsyncStream(longInterface.Path);
        _longOutputLength = longInterface.OutputLength;
        _longInputLength = longInterface.InputLength;

        if (shortInterface != null)
        {
            _short = HidNative.OpenAsyncStream(shortInterface.Path);
            _shortInputLength = shortInterface.InputLength;
        }
    }

    /// <summary>Returns the response parameters, or null on an error reply or timeout.</summary>
    public async Task<byte[]?> CallAsync(byte device, byte feature, byte function, params byte[] parameters)
    {
        byte functionAndId = (byte)((function << 4) | SoftwareId);

        var request = new byte[_longOutputLength];
        request[0] = LongReportId;
        request[1] = device;
        request[2] = feature;
        request[3] = functionAndId;
        parameters.CopyTo(request, 4);

        using var cancel = new CancellationTokenSource(Timeout);

        // Start reading first so a fast reply can't be missed.
        var readers = new List<Task<byte[]?>> { ReadReplyAsync(_long, _longInputLength, device, feature, functionAndId, cancel.Token) };
        if (_short != null) readers.Add(ReadReplyAsync(_short, _shortInputLength, device, feature, functionAndId, cancel.Token));

        try
        {
            await _long.WriteAsync(request, cancel.Token);
            return await await Task.WhenAny(readers);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Wait for both reads to unwind so none are pending when the next call begins.
            cancel.Cancel();
            await Task.WhenAll(readers.Select(r => r.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private static async Task<byte[]?> ReadReplyAsync(FileStream stream, int length, byte device, byte feature, byte functionAndId, CancellationToken cancel)
    {
        var buffer = new byte[length];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancel);
                if (read < 5 || buffer[1] != device) continue; // Other traffic, e.g. from G HUB.

                bool isError = buffer[2] is Hidpp10Error or Hidpp20Error;
                if (isError && buffer[3] == feature && buffer[4] == functionAndId) return null;
                if (buffer[2] == feature && buffer[3] == functionAndId) return buffer[4..read];
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException) when (cancel.IsCancellationRequested)
        {
            return null; // Cancelling overlapped I/O can surface as an aborted-operation error.
        }
    }

    public void Dispose()
    {
        _long.Dispose();
        _short?.Dispose();
    }
}

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

    public static async Task<BatteryReading> ReadAsync()
    {
        var devices = FindHidppDevices();
        if (devices.Count == 0) return new BatteryReading(ReadStatus.NoReceiver);

        foreach (var (longInterface, shortInterface) in devices)
        {
            try
            {
                using var channel = new HidppChannel(longInterface, shortInterface);
                if (await HidppMouse.ReadFirstAsync(channel) is { } reading) return reading;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unplugged mid-read, or unavailable. Move on; the next poll tries again.
            }
        }
        return new BatteryReading(ReadStatus.MouseUnreachable);
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
internal sealed class HidppChannel : IHidppChannel, IDisposable
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

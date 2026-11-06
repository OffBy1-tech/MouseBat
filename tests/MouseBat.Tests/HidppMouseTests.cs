using System.Text;
using MouseBat;

namespace MouseBat.Tests;

public class HidppMouseTests
{
    private const byte Keyboard = 0x00, Mouse = 0x03, Touchpad = 0x04, Trackball = 0x05;

    [Fact]
    public async Task ReadsVoltageMouse()
    {
        var channel = new FakeChannel().Add(1, new FakeDevice("G502 LIGHTSPEED", Mouse).Battery(0x1001, 0, 0x0F, 0xE9, 0x00));

        var reading = await HidppMouse.ReadFirstAsync(channel);

        Assert.Equal("G502 LIGHTSPEED", reading!.DeviceName);
        Assert.Equal(4073, reading.Millivolts);
        Assert.Equal(ReadStatus.Ok, reading.Status);
    }

    [Fact]
    public async Task ReadsBatteryStatusMouse()
    {
        var channel = new FakeChannel().Add(1, new FakeDevice("Older mouse", Mouse).Battery(0x1000, 0, 70, 50, 1));

        var reading = await HidppMouse.ReadFirstAsync(channel);

        Assert.Equal(70, reading!.Percent);
        Assert.True(reading.Charging);
    }

    [Fact]
    public async Task ReadsUnifiedBatteryThroughFunctionOne()
    {
        // The fake rejects every other function, so this fails if the wrong one is called.
        var channel = new FakeChannel().Add(1, new FakeDevice("Newer mouse", Mouse).Battery(0x1004, 1, 64, 4, 0, 0));

        var reading = await HidppMouse.ReadFirstAsync(channel);

        Assert.Equal(64, reading!.Percent);
        Assert.Null(reading.Millivolts);
    }

    [Fact]
    public async Task ReadsAdcMeasurementMouse()
    {
        var channel = new FakeChannel().Add(1, new FakeDevice("ADC mouse", Mouse).Battery(0x1F20, 0, 0x0F, 0xE9, 0x01));

        Assert.Equal(4073, (await HidppMouse.ReadFirstAsync(channel))!.Millivolts);
    }

    [Fact]
    public async Task FallsThroughToNextFeatureWhenOneIsUnusable()
    {
        var channel = new FakeChannel().Add(1, new FakeDevice("Two features", Mouse)
            .Battery(0x1000, 0, 0, 0, 0)
            .Battery(0x1004, 1, 64, 4, 0, 0));

        Assert.Equal(64, (await HidppMouse.ReadFirstAsync(channel))!.Percent);
    }

    [Fact]
    public async Task IncludesTrackballs()
    {
        var channel = new FakeChannel().Add(1, new FakeDevice("Trackball", Trackball).Battery(0x1004, 1, 64, 4, 0, 0));

        Assert.Equal("Trackball", (await HidppMouse.ReadFirstAsync(channel))!.DeviceName);
    }

    [Theory]
    [InlineData(Keyboard)]
    [InlineData(Touchpad)]
    public async Task SkipsDevicesThatAreNotMice(byte kind)
    {
        var channel = new FakeChannel()
            .Add(1, new FakeDevice("Not a mouse", kind).Battery(0x1004, 1, 10, 2, 0, 0))
            .Add(2, new FakeDevice("Real mouse", Mouse).Battery(0x1004, 1, 64, 4, 0, 0));

        Assert.Equal("Real mouse", (await HidppMouse.ReadFirstAsync(channel))!.DeviceName);
    }

    [Fact]
    public async Task SkipsMiceWithoutABattery()
    {
        var channel = new FakeChannel()
            .Add(1, new FakeDevice("Wired only", Mouse))
            .Add(2, new FakeDevice("Wireless", Mouse).Battery(0x1004, 1, 64, 4, 0, 0));

        Assert.Equal("Wireless", (await HidppMouse.ReadFirstAsync(channel))!.DeviceName);
    }

    [Fact]
    public async Task LowestSlotWinsWithTwoMice()
    {
        var channel = new FakeChannel()
            .Add(2, new FakeDevice("Second", Mouse).Battery(0x1004, 1, 64, 4, 0, 0))
            .Add(5, new FakeDevice("Fifth", Mouse).Battery(0x1004, 1, 30, 2, 0, 0));

        Assert.Equal("Second", (await HidppMouse.ReadFirstAsync(channel))!.DeviceName);
    }

    [Fact]
    public async Task FindsCabledMouseAddressedDirectly()
    {
        var channel = new FakeChannel().Add(0xFF, new FakeDevice("Cabled", Mouse).Battery(0x1001, 0, 0x0F, 0xE9, 0x80));

        var reading = await HidppMouse.ReadFirstAsync(channel);

        Assert.Equal("Cabled", reading!.DeviceName);
        Assert.True(reading.Charging);
        Assert.Null(reading.Percent);
    }

    [Fact]
    public async Task IgnoresPowerplaySlot()
    {
        var channel = new FakeChannel().Add(7, new FakeDevice("Candy companion chip", Mouse).Battery(0x1001, 0, 0x0F, 0xE9, 0x00));

        Assert.Null(await HidppMouse.ReadFirstAsync(channel));
    }

    [Fact]
    public async Task ReturnsNullWhenNothingAnswers() =>
        Assert.Null(await HidppMouse.ReadFirstAsync(new FakeChannel()));

    [Fact]
    public async Task ReadsNamesLongerThanOneReport()
    {
        const string longName = "G502 LIGHTSPEED Wireless Gaming Mouse";
        var channel = new FakeChannel().Add(1, new FakeDevice(longName, Mouse).Battery(0x1004, 1, 64, 4, 0, 0));

        Assert.Equal(longName, (await HidppMouse.ReadFirstAsync(channel))!.DeviceName);
    }

    private sealed class FakeChannel : IHidppChannel
    {
        private readonly Dictionary<byte, FakeDevice> _devices = [];

        public FakeChannel Add(byte slot, FakeDevice device)
        {
            _devices[slot] = device;
            return this;
        }

        public Task<byte[]?> CallAsync(byte device, byte feature, byte function, params byte[] parameters) =>
            Task.FromResult(_devices.TryGetValue(device, out var found) ? found.Call(feature, function, parameters) : null);
    }

    /// <summary>Simulates a HID++ 2.0 device: features sit at indexes in the order added.</summary>
    private sealed class FakeDevice(string name, byte kind)
    {
        private const ushort DeviceNameFeature = 0x0005;
        private const int NameChunkLength = 16;

        private readonly List<ushort> _features = [DeviceNameFeature];
        private readonly Dictionary<ushort, (byte Function, byte[] Response)> _batteries = [];

        public FakeDevice Battery(ushort feature, byte function, params byte[] response)
        {
            _features.Add(feature);
            _batteries[feature] = (function, response);
            return this;
        }

        public byte[]? Call(byte feature, byte function, byte[] parameters)
        {
            if (feature == 0)
            {
                if (function != 0) return null;
                int index = _features.IndexOf((ushort)((parameters[0] << 8) | parameters[1]));
                return [(byte)(index + 1)];
            }

            ushort id = _features[feature - 1];
            if (id != DeviceNameFeature)
            {
                var (expected, response) = _batteries[id];
                return function == expected ? response : null;
            }

            var nameBytes = Encoding.ASCII.GetBytes(name);
            return function switch
            {
                0 => [(byte)nameBytes.Length],
                1 => nameBytes[parameters[0]..Math.Min(parameters[0] + NameChunkLength, nameBytes.Length)],
                2 => [kind],
                _ => null,
            };
        }
    }
}

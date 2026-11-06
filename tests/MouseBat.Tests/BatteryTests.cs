using MouseBat;

namespace MouseBat.Tests;

public class BatteryCurveTests
{
    [Theory]
    [InlineData(4300, 100)]
    [InlineData(4186, 100)]
    [InlineData(3811, 50)]
    [InlineData(3835, 55)]
    [InlineData(3500, 0)]
    [InlineData(3000, 0)]
    public void ConvertsVoltageToPercent(int millivolts, int expected) =>
        Assert.Equal(expected, BatteryCurve.ToPercent(millivolts));

    [Fact]
    public void NeverRisesAsVoltageFalls()
    {
        int previous = 100;
        for (int mv = 4300; mv >= 3400; mv--)
        {
            int percent = BatteryCurve.ToPercent(mv);
            Assert.InRange(percent, 0, previous);
            previous = percent;
        }
    }
}

public class BatteryDecoderTests
{
    [Fact]
    public void BatteryStatusUsesReportedLevel() =>
        Assert.Equal(new BatterySample(70, false), BatteryDecoder.BatteryStatus([70, 50, 0]));

    [Theory]
    [InlineData(1)] // Recharging
    [InlineData(2)] // Almost full
    [InlineData(3)] // Full
    [InlineData(4)] // Slow recharge
    public void BatteryStatusRecognisesCharging(byte status) =>
        Assert.True(BatteryDecoder.BatteryStatus([70, 50, status])!.Charging);

    [Theory]
    [InlineData(5)] // Invalid battery
    [InlineData(6)] // Thermal error
    public void BatteryStatusErrorsAreNotCharging(byte status) =>
        Assert.False(BatteryDecoder.BatteryStatus([70, 50, status])!.Charging);

    [Theory]
    [InlineData(3, 90)]
    [InlineData(1, 50)]
    [InlineData(2, 50)]
    [InlineData(4, 20)]
    public void BatteryStatusInfersLevelFromChargeState(byte status, int expected) =>
        Assert.Equal(expected, BatteryDecoder.BatteryStatus([0, 0, status])!.Percent);

    [Fact]
    public void BatteryStatusWithNoLevelWhileDischargingIsUnusable() =>
        Assert.Null(BatteryDecoder.BatteryStatus([0, 0, 0]));

    [Fact]
    public void VoltageMatchesRealG502Report()
    {
        // Captured from a G502 Lightspeed: 0x0FE9 = 4073 mV, not charging.
        var sample = BatteryDecoder.BatteryVoltage([0x0F, 0xE9, 0x00])!;
        Assert.Equal(4073, sample.Millivolts);
        Assert.False(sample.Charging);
        Assert.Equal(BatteryCurve.ToPercent(4073), sample.Percent);
    }

    [Fact]
    public void VoltageReadsChargingFlag() =>
        Assert.True(BatteryDecoder.BatteryVoltage([0x0F, 0xE9, 0x80])!.Charging);

    [Fact]
    public void ChargingVoltageGivesNoPercent()
    {
        // Captured from a G502 Lightspeed on its cable: 4086 mV, while G HUB showed 43%.
        var sample = BatteryDecoder.BatteryVoltage([0x0F, 0xF6, 0x80])!;
        Assert.Null(sample.Percent);
        Assert.Equal(4086, sample.Millivolts);
    }

    [Fact]
    public void ChargingAdcGivesNoPercent() =>
        Assert.Null(BatteryDecoder.AdcMeasurement([0x0F, 0xF6, 0x03])!.Percent);

    [Fact]
    public void ZeroVoltageIsUnusableRatherThanEmpty() =>
        Assert.Null(BatteryDecoder.BatteryVoltage([0, 0, 0]));

    [Fact]
    public void UnifiedUsesStateOfCharge()
    {
        var sample = BatteryDecoder.UnifiedBattery([64, 4, 0, 0])!;
        Assert.Equal(64, sample.Percent);
        Assert.False(sample.Charging);
        Assert.Null(sample.Millivolts);
    }

    [Theory]
    [InlineData(8, 90)]
    [InlineData(4, 50)]
    [InlineData(2, 20)]
    [InlineData(1, 5)]
    [InlineData(0, 0)]
    public void UnifiedFallsBackToLevelFlag(byte level, int expected) =>
        Assert.Equal(expected, BatteryDecoder.UnifiedBattery([0, level, 0, 0])!.Percent);

    [Fact]
    public void UnifiedReadsCharging() =>
        Assert.True(BatteryDecoder.UnifiedBattery([64, 4, 1, 0])!.Charging);

    [Fact]
    public void AdcReadsVoltageAndCharging()
    {
        var sample = BatteryDecoder.AdcMeasurement([0x0F, 0xE9, 0x03])!;
        Assert.Equal(4073, sample.Millivolts);
        Assert.True(sample.Charging);
    }

    [Fact]
    public void AdcWithoutValidBitIsUnusable() =>
        Assert.Null(BatteryDecoder.AdcMeasurement([0x0F, 0xE9, 0x00]));

    [Fact]
    public void TruncatedReportsAreUnusable()
    {
        byte[] tooShort = [1, 2];
        Assert.Null(BatteryDecoder.BatteryStatus(tooShort));
        Assert.Null(BatteryDecoder.BatteryVoltage(tooShort));
        Assert.Null(BatteryDecoder.UnifiedBattery(tooShort));
        Assert.Null(BatteryDecoder.AdcMeasurement(tooShort));
    }
}

public class VoltageSmootherTests
{
    [Fact]
    public void IgnoresASingleSag()
    {
        var smoother = new VoltageSmoother();
        smoother.Add(3900, false);
        smoother.Add(3900, false);
        Assert.Equal(3900, smoother.Add(3600, false));
    }

    [Fact]
    public void ChargingChangeDiscardsOldSamples()
    {
        var smoother = new VoltageSmoother();
        smoother.Add(3900, false);
        smoother.Add(3900, false);
        Assert.Equal(4100, smoother.Add(4100, true));
    }
}

public class LowBatteryAlertTests
{
    [Fact]
    public void FiresOncePerLevelEvenWhenHoveringAtThreshold()
    {
        var alert = new LowBatteryAlert(20);
        Assert.Equal(AlertLevel.None, alert.Evaluate(50, false));
        Assert.Equal(AlertLevel.Low, alert.Evaluate(20, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(19, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(21, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(20, false));
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(10, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(9, false));
    }

    [Fact]
    public void ChargingSilencesAndRearms()
    {
        var alert = new LowBatteryAlert(20);
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(9, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(9, true));
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(9, false));
    }

    [Fact]
    public void ChargingWithNoLevelStillRearms()
    {
        var alert = new LowBatteryAlert(20);
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(9, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(null, true));
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(9, false));
    }

    [Fact]
    public void StartingAlreadyCriticalSkipsTheLowAlert()
    {
        var alert = new LowBatteryAlert(20);
        Assert.Equal(AlertLevel.Critical, alert.Evaluate(5, false));
        Assert.Equal(AlertLevel.None, alert.Evaluate(6, false));
    }

    [Fact]
    public void RearmsAfterClearRecovery()
    {
        var alert = new LowBatteryAlert(20);
        alert.Evaluate(20, false);
        alert.Evaluate(25, false);
        Assert.Equal(AlertLevel.Low, alert.Evaluate(20, false));
    }

    [Fact]
    public void ChangingThresholdReevaluates()
    {
        var alert = new LowBatteryAlert(20);
        Assert.Equal(AlertLevel.None, alert.Evaluate(25, false));
        alert.SetThreshold(30);
        Assert.Equal(AlertLevel.Low, alert.Evaluate(25, false));
    }
}

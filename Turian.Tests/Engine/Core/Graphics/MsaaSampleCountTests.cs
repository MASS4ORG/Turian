namespace Turian.Tests;

/// <summary>Tests how the requested MSAA sample count is capped to what the device supports.</summary>
public sealed class MsaaSampleCountTests
{
    const SampleCountFlags AllCounts =
        SampleCountFlags.Count64Bit | SampleCountFlags.Count32Bit | SampleCountFlags.Count16Bit
        | SampleCountFlags.Count8Bit | SampleCountFlags.Count4Bit | SampleCountFlags.Count2Bit
        | SampleCountFlags.Count1Bit;

    /// <summary>The chosen count is the highest the device supports that does not exceed the request.</summary>
    [Theory]
    [InlineData(4, AllCounts, SampleCountFlags.Count4Bit)]
    [InlineData(64, SampleCountFlags.Count8Bit | SampleCountFlags.Count1Bit, SampleCountFlags.Count8Bit)]
    [InlineData(8, SampleCountFlags.Count4Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count1Bit,
        SampleCountFlags.Count4Bit)]
    [InlineData(2, SampleCountFlags.Count4Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count1Bit,
        SampleCountFlags.Count2Bit)]
    [InlineData(1, SampleCountFlags.Count4Bit | SampleCountFlags.Count1Bit, SampleCountFlags.Count1Bit)]
    [InlineData(3, SampleCountFlags.Count4Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count1Bit,
        SampleCountFlags.Count2Bit)]
    [InlineData(16, SampleCountFlags.Count1Bit, SampleCountFlags.Count1Bit)]
    [InlineData(4, 0, SampleCountFlags.Count1Bit)]
    public void ClampsToHighestSupportedCountAtMostRequested(
        int requested,
        SampleCountFlags supported,
        SampleCountFlags expected
    )
    {
        Assert.Equal(expected, global::Turian.Engine.Core.Device.ClampSampleCount(requested, supported));
    }
}

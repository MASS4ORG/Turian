namespace Turian.Engine.Core;

/// <summary>One bounded set of timestamp queries for an offscreen target's single in-flight submission.</summary>
sealed unsafe class GpuTimestampQueries(Vk vk, Device device) : IDisposable
{
    const int queryCount = 4;
    static readonly string[] phaseNames = ["GPU preparation", "Scene render pass", "Readback copy"];
    QueryPool pool;
    uint validBits;
    double period;
    bool initialized;
    GpuSubmissionStatus failure;
    int written;

    /// <summary>Resets the query range once the previous submission no longer uses it.</summary>
    public void Begin(CommandBuffer commandBuffer)
    {
        if (!initialized)
        {
            Initialize();
        }
        written = 0;
        if (pool.Handle != 0)
        {
            vk.CmdResetQueryPool(commandBuffer, pool, 0, queryCount);
            Mark(commandBuffer, 0);
        }
    }

    /// <summary>Records completion up to a boundary in GPU submission order.</summary>
    public void Mark(CommandBuffer commandBuffer, uint index)
    {
        if (pool.Handle == 0 || index != written)
        {
            return;
        }
        vk.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.AllCommandsBit, pool, index);
        written++;
    }

    /// <summary>Reads a completed submission without waiting for query availability.</summary>
    public GpuSubmissionStatus Read(TimeSpan submissionEnvelope, out GpuPassTiming[] passes)
    {
        passes = [];
        if (pool.Handle == 0)
        {
            return failure;
        }
        if (written != queryCount)
        {
            return GpuSubmissionStatus.Unavailable;
        }
        var timestamps = stackalloc ulong[queryCount];
        var result = vk.GetQueryPoolResults(device.VkDevice, pool, 0, queryCount, queryCount * sizeof(ulong),
            timestamps, sizeof(ulong), QueryResultFlags.Result64Bit);
        return result == Result.Success && TryDecode(new ReadOnlySpan<ulong>(timestamps, queryCount),
            validBits, period, submissionEnvelope, out passes)
            ? GpuSubmissionStatus.Complete : GpuSubmissionStatus.Unavailable;
    }

    /// <summary>Converts wrapping device ticks into elapsed times within one submission.</summary>
    internal static bool TryDecode(ReadOnlySpan<ulong> timestamps, uint validBits, double period,
        TimeSpan submissionEnvelope, out GpuPassTiming[] passes)
    {
        passes = [];
        if (timestamps.Length != queryCount || validBits is 0 or > 64 || !double.IsFinite(period) || period <= 0 ||
            submissionEnvelope < TimeSpan.Zero ||
            submissionEnvelope.TotalNanoseconds >= Math.ScaleB(period, (int)validBits))
        {
            return false;
        }

        var mask = ulong.MaxValue >> (64 - (int)validBits);
        Span<double> durations = stackalloc double[queryCount - 1];
        var totalNanoseconds = 0d;
        for (var i = 0; i < durations.Length; i++)
        {
            var ticks = unchecked(timestamps[i + 1] - timestamps[i]) & mask;
            durations[i] = ticks * period;
            totalNanoseconds += durations[i];
        }
        // A period of device timestamp rounding and one TimeSpan tick may straddle the host bounds.
        if (!double.IsFinite(totalNanoseconds) || totalNanoseconds > submissionEnvelope.TotalNanoseconds + period + 100)
        {
            return false;
        }

        var offset = 0d;
        passes = new GpuPassTiming[durations.Length];
        for (var i = 0; i < durations.Length; i++)
        {
            passes[i] = new GpuPassTiming(phaseNames[i], offset, durations[i] / 1_000_000d);
            offset += passes[i].DurationMilliseconds;
        }
        return true;
    }

    void Initialize()
    {
        initialized = true;
        failure = GpuSubmissionStatus.TimestampUnsupported;
        period = device.GetProperties().Limits.TimestampPeriod;
        uint count = 0;
        var physicalDevice = device.VkPhysicalDevice;
        vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref count, pointer);
        }
        var family = device.GraphicsQueueFamilyIndex;
        if (family >= count || !double.IsFinite(period) || period <= 0)
        {
            return;
        }
        validBits = families[family].TimestampValidBits;
        if (validBits is 0 or > 64)
        {
            return;
        }

        QueryPoolCreateInfo info = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = queryCount
        };
        if (vk.CreateQueryPool(device.VkDevice, in info, null, out var created) == Result.Success)
        {
            pool = created;
        }
        failure = GpuSubmissionStatus.Unavailable;
    }

    /// <summary>Releases the query pool after the caller has drained this target's submissions.</summary>
    public void Dispose()
    {
        if (pool.Handle != 0)
        {
            vk.DestroyQueryPool(device.VkDevice, pool, null);
            pool = default;
        }
    }
}

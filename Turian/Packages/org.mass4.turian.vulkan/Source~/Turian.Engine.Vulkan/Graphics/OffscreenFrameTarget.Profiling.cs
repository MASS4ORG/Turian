namespace Turian.Engine.Core;

public partial class OffscreenFrameTarget
{
#if !TURIAN_NO_CPU_MARKERS
    Profiler.GpuCapture gpuCapture;
    GpuTimestampQueries? gpuQueries;
    long gpuSubmitStart;

    void BeginGpuCapture(bool captureGpu)
    {
        gpuCapture = captureGpu ? Profiler.BeginGpuSubmission() : default;
        if (!gpuCapture.IsActive)
        {
            return;
        }
        gpuQueries ??= new GpuTimestampQueries(vk, device);
        gpuQueries.Begin(commandBuffer);
    }

    void MarkGpuBoundary(uint index)
    {
        if (gpuCapture.IsActive)
        {
            gpuQueries!.Mark(commandBuffer, index);
        }
    }

    void CompleteGpuCapture()
    {
        if (gpuCapture.IsActive)
        {
            var status = gpuQueries!.Read(Stopwatch.GetElapsedTime(gpuSubmitStart), out var passes);
            gpuCapture.Complete(status, passes, CaptureGraphicsMemory());
            gpuCapture = default;
        }
    }
#endif
}

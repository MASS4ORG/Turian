namespace Turian.Engine.Core;

public partial class OffscreenFrameTarget
{
    // These categories are partial: texture bytes are payload estimates across devices, while the
    // attachment and readback counters are allocation sizes for this target only. Engine buffers
    // cover Buffer-owned allocations on this device, including temporary staging buffers.
    internal GpuMemoryCounter[] CaptureGraphicsMemory() =>
    [
        new("Cached texture asset payload (all devices)", TextureAsset.UploadedBytes),
        new("Engine buffers (device)", device.BufferAllocationBytes),
        new("Offscreen attachments", attachmentAllocationBytes),
        new("Offscreen readback buffer", stagingAllocationBytes)
    ];
}

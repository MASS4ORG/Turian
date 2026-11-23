namespace Turian.Engine.Core;

public partial class Device
{
    long bufferAllocationBytes;

    internal ulong BufferAllocationBytes => (ulong)Math.Max(0, Interlocked.Read(ref bufferAllocationBytes));

    internal void AddBufferAllocation(ulong bytes) => Interlocked.Add(ref bufferAllocationBytes, (long)bytes);

    internal void RemoveBufferAllocation(ulong bytes) => Interlocked.Add(ref bufferAllocationBytes, -(long)bytes);
}

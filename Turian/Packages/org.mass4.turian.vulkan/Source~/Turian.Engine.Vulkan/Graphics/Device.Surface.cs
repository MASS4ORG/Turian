namespace Turian.Engine.Core;

public unsafe partial class Device
{
    void CreateSurface()
    {
        if (!vk.TryGetInstanceExtension(instance, out khrSurface))
        {
            throw new NotSupportedException("KHR_surface extension not found.");
        }

        surface = surfaceHost!.CreateSurface(vk, instance);
    }
}

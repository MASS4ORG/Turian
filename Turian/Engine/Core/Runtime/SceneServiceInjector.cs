namespace Turian.Engine.Core;

static class SceneServiceInjector
{
    static readonly ConcurrentDictionary<Type, (PropertyInfo Property, bool Optional)[]> Properties = new();

    internal static void ReleaseCollectible() => CollectibleAssemblies.RemoveReleased(Properties, static type => type);

    internal static void Inject(object instance, IServiceProvider? services, bool allowMissingServices)
    {
        if (services is null)
        {
            return;
        }

        if (instance is Component component && GeneratedComponentMetadata.TryGet(instance.GetType(), out var metadata))
        {
            metadata.Inject(component, services, allowMissingServices);
            return;
        }

        if (instance is Node node && GeneratedNodeMetadata.TryGet(instance.GetType(), out var nodeMetadata))
        {
            nodeMetadata.Inject(node, services, allowMissingServices);
            return;
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException($"No generated injection metadata for '{instance.GetType().FullName}'.");
        }

        var properties = Properties.GetOrAdd(instance.GetType(), static type =>
            [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static property => (Property: property, Attribute: property.GetCustomAttribute<InjectServiceAttribute>(inherit: true)))
                .Where(static entry => entry.Attribute is not null)
                .Select(static entry => (entry.Property, entry.Attribute!.Optional))]);

        foreach (var (property, optional) in properties)
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length != 0 ||
                !property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            {
                throw new InvalidOperationException(
                    $"Injected property '{instance.GetType().FullName}.{property.Name}' must have a setter and [JsonIgnore].");
            }

            var service = services.GetService(property.PropertyType);
            if (service is null && !optional && !allowMissingServices)
            {
                throw new InvalidOperationException(
                    $"Service '{property.PropertyType.FullName}' required by '{instance.GetType().FullName}' is not registered.");
            }

            property.SetValue(instance, service);
        }
    }
}

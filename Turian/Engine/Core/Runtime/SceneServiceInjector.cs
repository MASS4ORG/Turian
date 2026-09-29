namespace Turian.Engine.Core;

static class SceneServiceInjector
{
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    internal static void Inject(object instance, IServiceProvider? services)
    {
        if (services is null) return;

        var properties = Properties.GetOrAdd(instance.GetType(), static type =>
            [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(property => property.IsDefined(typeof(InjectServiceAttribute), inherit: true))]);

        foreach (var property in properties)
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length != 0 ||
                !property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
                throw new InvalidOperationException(
                    $"Injected property '{instance.GetType().FullName}.{property.Name}' must have a setter and [JsonIgnore].");

            var service = services.GetService(property.PropertyType)
                          ?? throw new InvalidOperationException(
                              $"Service '{property.PropertyType.FullName}' required by '{instance.GetType().FullName}' is not registered.");
            property.SetValue(instance, service);
        }
    }
}

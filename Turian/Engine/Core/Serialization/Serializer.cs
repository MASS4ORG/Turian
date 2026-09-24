namespace Turian.Engine.Core;

/// <summary>
/// AMOject serializer
/// </summary>
public static class Serializer
{
    static JsonSerializerOptions? jsonOptions;
    static readonly Lock optionsGate = new();

    /// <summary>
    /// Default options for (de)serializing Objects
    /// </summary>
    /// <remarks>
    /// The options are published only once fully built. Callers come from several threads — the UI,
    /// the asset watcher, and the play-mode scene copy — and a half-initialised instance would be
    /// missing its converters.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions
    {
        get
        {
            var options = jsonOptions;
            if (options is not null) return options;

            lock (optionsGate)
            {
                if (jsonOptions is not null) return jsonOptions;

                options = new JsonSerializerOptions
                {
                    IgnoreReadOnlyProperties = true,
                    IncludeFields = true,
                    WriteIndented = true
                };

                options.Converters.Add(new ComponentJsonConverter());
                options.Converters.Add(new AssetReferenceJsonConverterFactory());
                options.Converters.Add(new Color32JsonConverter());

                var amObjectType = typeof(IdClass);
                var derivedTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try
                        {
                            return a.GetTypes();
                        }
                        catch
                        {
                            return [];
                        }
                    })
                    .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(amObjectType));

                foreach (var derivedType in derivedTypes)
                {
                    var converterType = typeof(ObjectJsonSerializer<>).MakeGenericType(derivedType);
                    if (Activator.CreateInstance(converterType) is JsonConverter converterInstance)
                        options.Converters.Add(converterInstance);
                }

                jsonOptions = options;
                return options;
            }
        }
    }

    /// <summary>
    /// Reset the default options for (de)serializing Objects when the UserCode is reloaded.
    /// Also re-scans loaded assemblies in <see cref="TypeRegistry"/> so newly compiled
    /// user types become resolvable by their stable <see cref="TypeIdAttribute"/> ids.
    /// </summary>
    public static void ResetOptions()
    {
        lock (optionsGate)
        {
            jsonOptions = null;
        }

        TypeRegistry.Reset();
    }

    /// <summary>
    /// Load the Object content from the given path
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="absolutePath"></param>
    /// <returns></returns>
    public static T? Load<T>(string absolutePath)
    {
        var json = File.ReadAllText(absolutePath);
        return LoadData<T>(json);
    }

    /// <summary>
    /// Load the Object content from the given json
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    /// <returns></returns>
    public static T? LoadData<T>(string data)
    {
        return JsonSerializer.Deserialize<T>(data, JsonOptions);
    }

    /// <summary>
    /// Save the Object content to the given path
    /// </summary>
    /// <param name="absolutePath"></param>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    public static void Save<T>(string absolutePath, T value)
    {
        File.WriteAllText(absolutePath, Serialize(value));
    }

    /// <summary>
    /// Saves the specified object to a file asynchronously.
    /// </summary>
    public static async Task SaveAsync<T>(string absolutePath, T value)
    {
        await File.WriteAllTextAsync(absolutePath, Serialize(value));
    }

    /// <summary>
    /// Serialize the Object content to a json string
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static string Serialize<T>(T value) //where T : IdClass
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }
}

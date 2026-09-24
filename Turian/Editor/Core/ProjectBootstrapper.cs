namespace Turian.Editor.Core;

/// <summary>
/// Creates the on-disk scaffold for a new project: directories, default scene,
/// meta file, settings assets, and bootstrap source.
/// </summary>
public sealed class ProjectBootstrapper
{
    const string defaultProjectTitle = "New Project";
    const string defaultSceneFileName = "scene-01.prefab";
    const string assetsDirectoryName = "Assets";

    const string defaultGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Numerics;
        global using System.Threading.Tasks;
        global using Microsoft.Extensions.Logging;
        global using Silk.NET.Input;
        global using Turian;
        global using Turian.Engine.Core;
        global using Turian.Engine.UI;

        """;

    /// <summary>
    /// Creates a new project scaffold under <paramref name="projectDirectory"/>.
    /// Returns the absolute project folder, which is what opening the project takes,
    /// or <see langword="null"/> on failure.
    /// </summary>
    public async Task<string?> CreateAsync(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        try
        {
            Directory.CreateDirectory(projectDirectory);
            var assetsDir = Path.Combine(projectDirectory, assetsDirectoryName);
            Directory.CreateDirectory(assetsDir);

            var title = Path.GetFileName(
                projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(title)) title = defaultProjectTitle;

            var startupSceneId = Guid.NewGuid();
            var sceneFilePath = Path.Combine(assetsDir, defaultSceneFileName);

            await Serializer.SaveAsync(sceneFilePath, CreateDefaultScene());

            var sceneMeta = new Prefab { Id = startupSceneId, RelativePath = sceneFilePath };
            await Serializer.SaveAsync($"{sceneFilePath}.meta", sceneMeta);

            var settings = new AppSettings { Title = title, ProjectAbsoluteDir = Path.GetFullPath(projectDirectory) };

            ProjectSettingsFiles.Create(settings, new PlayerSettings
            {
                ProductName = title,
                Author = Environment.UserName,
                ApplicationIdentifier = $"com.turian.{SanitizeIdentifierSegment(title)}",
                Version = "0.1.0",
                StartupScene = new AssetReference<Prefab>(sceneMeta)
            });
            ProjectSettingsFiles.Create(settings, new InputSettings());
            ProjectSettingsFiles.Create(settings, new GraphicsSettings());

            // Scripts get the engine, its attributes and the input key codes without per-file usings.
            var globalsPath = Path.Combine(assetsDir, "Globals.cs");
            if (!File.Exists(globalsPath))
                await File.WriteAllTextAsync(globalsPath, defaultGlobalUsings);

            var bootstrapPath = Path.Combine(assetsDir, "Game.cs");
            if (!File.Exists(bootstrapPath))
                await File.WriteAllTextAsync(bootstrapPath,
                    "namespace Usercode;\n\npublic static class Game\n{\n}\n");

            return settings.ProjectAbsoluteDir;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Project creation failed");
            return null;
        }
    }

    /// <summary>
    /// Validates and parses the <c>--project</c> CLI argument from
    /// <paramref name="args"/>, returning the path or <see langword="null"/>.
    /// </summary>
    public static string? ParseProjectArgument(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, "--project");
        return index >= 0 && index < args.Length - 1
            ? Path.GetFullPath(args[index + 1])
            : null;
    }

    /// <summary>Strips non-alphanumeric chars for use in an application identifier.</summary>
    public static string SanitizeIdentifierSegment(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLower(ch, CultureInfo.InvariantCulture) : '-')
            .ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "game" : sanitized;
    }

    static Node CreateDefaultScene()
    {
        var root = new Node { Name = "Main Scene" };

        var cameraNode = new Node
        {
            Name = "Camera",
            Transform =
            {
                Position = new Vector3(0f, 2f, -6f)
            }
        };
        cameraNode.AddComponent<CameraComponent>();

        var light = new Node
        {
            Name = "Light",
            Transform =
            {
                Position = new Vector3(2f, 4f, -2f)
            }
        };
        light.AddComponent(LightComponent.CreatePointLight(1.5f, new Vector4(1f, 1f, 1f, 1f)));

        var box = new Node { Name = "Box" };
        box.AddComponent<MeshComponent>();

        root.Children.Add(cameraNode);
        root.Children.Add(light);
        root.Children.Add(box);
        return root;
    }
}

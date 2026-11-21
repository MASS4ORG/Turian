using System.ComponentModel.DataAnnotations;
using EditorRange = MASS4.Attributes.RangeAttribute;
using EditorRequired = MASS4.Attributes.RequiredAttribute;

namespace Turian.Editor.Core;

/// <summary>A validation error in authored DataAsset content.</summary>
/// <param name="Path">The source asset path.</param>
/// <param name="Member">The invalid field or property, when known.</param>
/// <param name="Message">What the author needs to correct.</param>
public sealed record DataAssetValidationError(string Path, string? Member, string Message);

/// <summary>Validates authored DataAssets using inspector attributes and asset-defined rules.</summary>
public static class DataAssetValidation
{
    /// <summary>Collects the errors for a staged asset before the editor writes it.</summary>
    public static IReadOnlyList<DataAssetValidationError> Validate(DataAsset asset, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var errors = new List<DataAssetValidationError>();
        foreach (var member in asset.GetType().GetMembers(BindingFlags.Instance | BindingFlags.Public))
        {
            if (member is not FieldInfo and not PropertyInfo { GetMethod.IsPublic: true }
                || member is PropertyInfo property && property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var attributes = Attribute.GetCustomAttributes(member, inherit: true)
                .Where(static attribute => attribute is EditorRequired or EditorRange or ValidationAttribute).ToArray();
            if (attributes.Length == 0)
            {
                continue;
            }

            var value = member is PropertyInfo getter ? getter.GetValue(asset) : ((FieldInfo)member).GetValue(asset);
            ValidateMember(value, member.Name, attributes, path, errors);
            ValidateValue(value, new ValidationContext(asset) { MemberName = member.Name },
                attributes.OfType<ValidationAttribute>(), path, errors);
        }

        var assetContext = new ValidationContext(asset);
        ValidateValue(asset, assetContext, Attribute.GetCustomAttributes(asset.GetType(), typeof(ValidationAttribute))
            .OfType<ValidationAttribute>(), path, errors);
        if (asset is IValidatableObject custom)
        {
            AddErrors(custom.Validate(assetContext), path, null, errors);
        }

        return errors;
    }

    /// <summary>Checks DataAsset sources, identities, inherited values, and references in the supplied asset roots.</summary>
    public static IReadOnlyList<DataAssetValidationError> ValidateProject(IEnumerable<string> assetRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetRoots);

        var errors = new List<DataAssetValidationError>();
        var sources = ReadSources(assetRoots, errors, cancellationToken);
        var assets = new Dictionary<Guid, DataAsset>();
        foreach (var (id, source) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var asset = DataAsset.LoadPayload(source.Json, id,
                    baseId => sources.TryGetValue(baseId, out var baseSource) ? baseSource.Json : null)
                    ?? throw new InvalidDataException("The DataAsset payload could not be loaded.");
                asset.Id = id;
                assets.Add(id, asset);
            }
            catch (Exception error) when (IsAuthoringError(error))
            {
                errors.Add(new(source.Path, null, error.GetBaseException().Message));
            }
        }

        foreach (var (id, asset) in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = sources[id].Path;
            try
            {
                var unresolved = ObjectReferences.ResolveDataAsset(asset, assets.GetValueOrDefault);
                if (unresolved > 0)
                {
                    errors.Add(new(path, null, $"Could not resolve {unresolved} DataAsset reference(s)."));
                }
            }
            catch (Exception error) when (IsAuthoringError(error))
            {
                errors.Add(new(path, null, error.GetBaseException().Message));
            }
        }

        foreach (var (id, asset) in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = sources[id].Path;
            try
            {
                errors.AddRange(Validate(asset, path));
            }
            catch (Exception error) when (IsAuthoringError(error))
            {
                errors.Add(new(path, null, error.GetBaseException().Message));
            }
        }

        return errors;
    }

    static Dictionary<Guid, (string Path, string Json)> ReadSources(IEnumerable<string> assetRoots,
        List<DataAssetValidationError> errors, CancellationToken cancellationToken)
    {
        var paths = assetRoots.Where(Directory.Exists).SelectMany(static root =>
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => GenericAssetImporter.IsDataAssetPath(path) && !AssetDatabase.IsInTildeFolder(path, root)))
            .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var sources = new Dictionary<Guid, (string Path, string Json)>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metaPath = $"{path}.meta";
                if (!File.Exists(metaPath))
                {
                    throw new InvalidDataException("The DataAsset has no .meta file. Import it to assign its identity.");
                }
                if (Asset.Load(metaPath) is not { Id: var id } || id == Guid.Empty)
                {
                    throw new InvalidDataException("The DataAsset .meta file does not declare a valid asset identity.");
                }
                if (!sources.TryAdd(id, (path, File.ReadAllText(path))))
                {
                    throw new InvalidDataException($"Asset identity {id} is also used by {sources[id].Path}.");
                }
            }
            catch (Exception error) when (IsAuthoringError(error))
            {
                errors.Add(new(path, null, error.GetBaseException().Message));
            }
        }

        return sources;
    }

    static bool IsAuthoringError(Exception error) => error is IOException or InvalidDataException
        or InvalidOperationException or JsonException or UnauthorizedAccessException or TargetInvocationException
        or FormatException or NotSupportedException;

    static void ValidateMember(object? value, string member, Attribute[] attributes, string path,
        List<DataAssetValidationError> errors)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                case EditorRequired required when value is null or string { Length: 0 } or ICollection { Count: 0 }:
                    errors.Add(new(path, member, required.Message ?? $"{member} is required."));
                    break;
                case EditorRange range when value is not null && !IsInRange(value, range):
                    errors.Add(new(path, member, FormattableString.Invariant(
                        $"{member} must be a number between {range.Min} and {range.Max}.")));
                    break;
            }
        }
    }

    static bool IsInRange(object value, EditorRange range)
    {
        if (value is Enum || value is not IConvertible number
            || number.GetTypeCode() is < TypeCode.SByte or > TypeCode.Decimal)
        {
            return false;
        }

        var numeric = number.ToDouble(CultureInfo.InvariantCulture);
        return numeric >= range.Min && numeric <= range.Max;
    }

    static void ValidateValue(object? value, ValidationContext context, IEnumerable<ValidationAttribute> attributes,
        string path, List<DataAssetValidationError> errors)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateValue(value, context, results, attributes))
        {
            AddErrors(results, path, context.MemberName, errors);
        }
    }

    static void AddErrors(IEnumerable<ValidationResult> results, string path, string? fallbackMember,
        List<DataAssetValidationError> errors)
    {
        foreach (var result in results)
        {
            foreach (var member in result.MemberNames.DefaultIfEmpty(fallbackMember))
            {
                errors.Add(new(path, member, result.ErrorMessage ?? "Invalid value."));
            }
        }
    }
}

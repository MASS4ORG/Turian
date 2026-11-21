using System.ComponentModel.DataAnnotations;

namespace Turian.Tests;

/// <summary>Validation uses the authored payload before saving and the source chain for project reports.</summary>
[Collection(SerialTests.Name)]
public sealed class DataAssetValidationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-asset-validation-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Required properties, ranged fields and cross-member errors have actionable locations.</summary>
    [Fact]
    public void ReportsMemberAndAssetRules()
    {
        var asset = new ValidatedAsset { Points = 11 };
        Assert.Collection(DataAssetValidation.Validate(asset, "stats.dataasset")
                .OrderBy(static error => error.Member, StringComparer.Ordinal),
            static error => Assert.Equal(nameof(ValidatedAsset.Label), error.Member),
            static error => Assert.Equal(nameof(ValidatedAsset.Points), error.Member),
            static error => Assert.Equal("Budget exceeded.", error.Message));
    }

    /// <summary>A rejected inspector edit leaves the previously saved file intact.</summary>
    [Fact]
    public void InvalidEditLeavesFileIntact()
    {
        var path = Path.Combine(root, "stats.dataasset");
        var asset = new ValidatedAsset { Label = "Starter", Points = 3 };
        Serializer.Save<DataAsset>(path, asset);
        var before = File.ReadAllText(path);
        asset.Points = 11;
        var inspections = new AssetInspectionService(null!, new AssetTypeCatalog(), new SettingsService(),
            null!, NullLogger.Instance);
        var inspection = new AssetInspection(new DataAssetAsset { Id = asset.Id }, path, asset, "Stats", true);

        Assert.False(inspections.Apply(inspection));
        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>A project report reads current source and validates a variant's inherited data.</summary>
    [Fact]
    public void ProjectReportValidatesSourceVariants()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets"));
        var basePath = Path.Combine(assets.FullName, "base.dataasset");
        var asset = new ValidatedAsset { Label = "Starter", Points = 3 };
        var variantPath = Path.Combine(assets.FullName, "variant.dataasset");
        var variantId = Guid.NewGuid();
        Serializer.Save<DataAsset>(basePath, asset);
        File.WriteAllText(variantPath, DataAssetVariants.Create(asset.Id, File.ReadAllText(basePath), variantId));
        Serializer.Save($"{basePath}.meta", new DataAssetAsset { Id = asset.Id, RelativePath = basePath });
        Serializer.Save($"{variantPath}.meta", new DataAssetAsset { Id = variantId, RelativePath = variantPath });
        Assert.Empty(DataAssetValidation.ValidateProject([assets.FullName], TestContext.Current.CancellationToken));

        asset.Points = 0;
        Serializer.Save<DataAsset>(basePath, asset);
        Assert.Collection(DataAssetValidation.ValidateProject([assets.FullName], TestContext.Current.CancellationToken),
            error => Assert.Equal(basePath, error.Path),
            error => Assert.Equal(variantPath, error.Path));
    }

    /// <summary>Reports missing, invalid and duplicate metadata for source files.</summary>
    [Fact]
    public void ReportsInventoryErrorsBeforeLoadingPayloads()
    {
        var good = Path.Combine(root, "a.dataasset");
        var duplicate = Path.Combine(root, "b.dataasset");
        var malformed = Path.Combine(root, "c.dataasset");
        var unimported = Path.Combine(root, "d.dataasset");
        var asset = new ValidatedAsset { Label = "Starter", Points = 3 };
        foreach (var path in new[] { good, duplicate, malformed, unimported })
        {
            Serializer.Save<DataAsset>(path, asset);
        }
        var metadata = new DataAssetAsset { Id = asset.Id };
        Serializer.Save($"{good}.meta", metadata);
        Serializer.Save($"{duplicate}.meta", metadata);
        File.WriteAllText($"{malformed}.meta", "invalid json");

        Assert.Collection(DataAssetValidation.ValidateProject([root], TestContext.Current.CancellationToken),
            error => Assert.Equal(duplicate, error.Path),
            error => Assert.Equal(malformed, error.Path),
            error => Assert.Equal(unimported, error.Path));
    }

    /// <summary>Resolves authored references against source identities before validating Required members.</summary>
    [Fact]
    public void ResolvesRequiredReferencesFromTheSameSourceInventory()
    {
        var target = new ValidatedAsset { Label = "Starter", Points = 3 };
        var owner = new ReferenceAsset { Target = target };
        var targetPath = Path.Combine(root, "target.dataasset");
        var ownerPath = Path.Combine(root, "owner.dataasset");
        Serializer.Save<DataAsset>(targetPath, target);
        Serializer.Save<DataAsset>(ownerPath, owner);
        Serializer.Save($"{targetPath}.meta", new DataAssetAsset { Id = target.Id });
        Serializer.Save($"{ownerPath}.meta", new DataAssetAsset { Id = owner.Id });
        Assert.Empty(DataAssetValidation.ValidateProject([root], TestContext.Current.CancellationToken));

        File.Delete(targetPath);
        var errors = DataAssetValidation.ValidateProject([root], TestContext.Current.CancellationToken);
        Assert.Contains(errors, static error => error.Member == nameof(ReferenceAsset.Target));
    }

    /// <summary>Variants reject stale bases and edits that cannot survive a save and reopen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VariantSavePreservesReopenBehavior(bool editBaseOnDisk)
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        ProjectPackages.OptOutOfDefault(root, ProjectPackages.RenderBrick);
        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = root };
        settings.Set(app);
        using var build = new BuildManager(app, NullLogger.Instance);
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, settings, build);
        importer.StopMonitoring();
        var basePath = Path.Combine(assets, "base.dataasset");
        var variantPath = Path.Combine(assets, "variant.dataasset");
        var asset = new ValidatedAsset { Label = "Starter", Points = 3 };
        Serializer.Save<DataAsset>(basePath, asset);
        Serializer.Save($"{basePath}.meta", new DataAssetAsset { Id = asset.Id, RelativePath = basePath });
        var variantId = Guid.NewGuid();
        var original = DataAssetVariants.Create(asset.Id, File.ReadAllText(basePath), variantId);
        File.WriteAllText(variantPath, original);
        Serializer.Save($"{variantPath}.meta", new DataAssetAsset { Id = variantId, RelativePath = variantPath });
        database.BuildDatabase(assets);
        var inspections = new AssetInspectionService(importer, new AssetTypeCatalog(), settings,
            new RuntimeAssetLoader(database), NullLogger.Instance);

        var inspection = inspections.Inspect(variantPath);
        Assert.NotNull(inspection);
        var staged = Assert.IsType<ValidatedAsset>(inspection.Target);
        Assert.Equal(3, staged.Points);
        if (editBaseOnDisk)
        {
            staged.Label = "Edited";
            asset.Points = 4;
            Serializer.Save<DataAsset>(basePath, asset);
        }
        else
        {
            staged.Description = null;
        }

        Assert.False(inspections.Apply(inspection));
        Assert.Equal(original, File.ReadAllText(variantPath));
        var reopened = inspections.Inspect(variantPath);
        Assert.NotNull(reopened);
        var restored = Assert.IsType<ValidatedAsset>(reopened.Target);
        Assert.Equal(editBaseOnDisk ? 4 : 3, restored.Points);
        Assert.Equal("Default description", restored.Description);

        restored.Description = "An override";
        Assert.True(inspections.Apply(reopened));
        Assert.Equal("An override", Assert.IsType<ValidatedAsset>(inspections.Inspect(variantPath)?.Target).Description);
    }

    /// <summary>A required link to another project asset.</summary>
    [TypeId("39ff0193-c360-42f8-ac0c-1f801fa51508")]
    public sealed class ReferenceAsset : DataAsset
    {
        /// <summary>The referenced asset.</summary>
        [MASS4.Attributes.Required]
        public ValidatedAsset? Target { get; set; }
    }

    /// <summary>Authored payload with property, field and cross-member validation.</summary>
    [TypeId("8658605f-143c-4505-8d1e-74b459b09587")]
    public sealed class ValidatedAsset : DataAsset, IValidatableObject
    {
        /// <summary>The display name.</summary>
        [MASS4.Attributes.Required]
        public string? Label { get; set; }

        /// <summary>An optional value that defaults to authored text when omitted.</summary>
        public string? Description { get; set; } = "Default description";

        /// <summary>The points the asset costs.</summary>
        [MASS4.Attributes.Range(1, 10)]
        public int Points;

        /// <inheritdoc/>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => Points > 9
            ? [new ValidationResult("Budget exceeded.", [nameof(Points)])]
            : [];
    }
}

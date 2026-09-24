namespace Turian.Tests;

/// <summary>
/// Verifies that the JSON serializers emit and resolve the stable <c>__TypeId</c>
/// discriminator (instead of assembly-qualified type names), and that polymorphic
/// round-trip works through both <see cref="ObjectJsonSerializer{T}"/> and
/// <see cref="ComponentJsonConverter"/>.
/// </summary>
public class TypeIdSerializationTests
{
    [TypeId("b0000001-0000-4000-8000-000000000001")]
    internal class BaseSample : IdClass
    {
        public int BaseValue { get; set; } = 1;
    }

    [TypeId("b0000001-0000-4000-8000-000000000002")]
    internal sealed class DerivedSample : BaseSample
    {
        public int DerivedValue { get; set; } = 2;
    }

    [TypeId("b0000001-0000-4000-8000-000000000003")]
    internal sealed class SampleAsset : Asset
    {
        public string Label { get; set; } = "default";
    }

    [TypeId("b0000001-0000-4000-8000-000000000004")]
    internal sealed class SampleComponent : Component
    {
        public float Speed { get; set; } = 1f;
    }

    static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions { IncludeFields = true };
        opts.Converters.Add(new ComponentJsonConverter());
        opts.Converters.Add(new ObjectJsonSerializer<BaseSample>());
        opts.Converters.Add(new ObjectJsonSerializer<DerivedSample>());
        opts.Converters.Add(new ObjectJsonSerializer<SampleAsset>());
        return opts;
    }

    /// <summary>The serialized payload uses <c>__TypeId</c> with a Guid, not <c>__Type</c>.</summary>
    [Fact]
    public void SerializedPayloadUsesTypeIdDiscriminator()
    {
        var sample = new BaseSample();
        var json = JsonSerializer.Serialize(sample, BuildOptions());

        Assert.Contains("\"__TypeId\":\"b0000001-0000-4000-8000-000000000001\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"__Type\"", json, StringComparison.Ordinal);
    }

    /// <summary>A subclass declared via <c>__TypeId</c> deserializes back to the concrete subclass.</summary>
    [Fact]
    public void PolymorphicDeserializationProducesConcreteSubclass()
    {
        var options = BuildOptions();
        var derived = new DerivedSample { BaseValue = 7, DerivedValue = 42 };

        // Serialize through the base-typed converter so polymorphism is exercised.
        var json = JsonSerializer.Serialize<BaseSample>(derived, options);
        var roundTripped = JsonSerializer.Deserialize<BaseSample>(json, options);

        Assert.IsType<DerivedSample>(roundTripped);
        Assert.Equal(7, roundTripped.BaseValue);
        Assert.Equal(42, ((DerivedSample)roundTripped).DerivedValue);
    }

    /// <summary>An <see cref="Asset"/>-typed member is serialized as a stub carrying the asset's TypeId and instance Id.</summary>
    [Fact]
    public void AssetMemberSerializesAsTypeIdAndInstanceIdStub()
    {
        var holder = new AssetHolder
        {
            Asset = new SampleAsset { Label = "ignored when serialized as a stub" }
        };
        var holderId = holder.Asset.Id;

        var options = BuildOptions();
        options.Converters.Add(new ObjectJsonSerializer<AssetHolder>());

        var json = JsonSerializer.Serialize(holder, options);

        Assert.Contains("\"__TypeId\":\"b0000001-0000-4000-8000-000000000003\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"Id\":\"{holderId}\"", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<AssetHolder>(json, options);
        Assert.NotNull(roundTripped);
        Assert.IsType<SampleAsset>(roundTripped.Asset);
        Assert.Equal(holderId, roundTripped.Asset.Id);
    }

    /// <summary><see cref="ComponentJsonConverter"/> round-trips a Component through its TypeId.</summary>
    [Fact]
    public void ComponentConverterRoundTripsViaTypeId()
    {
        var options = BuildOptions();
        var component = new SampleComponent { Speed = 12.5f };

        var json = JsonSerializer.Serialize<Component>(component, options);
        Assert.Contains("\"__TypeId\":\"b0000001-0000-4000-8000-000000000004\"", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<Component>(json, options);
        Assert.IsType<SampleComponent>(roundTripped);
        Assert.Equal(12.5f, ((SampleComponent)roundTripped).Speed);
    }

    /// <summary>Reading JSON whose TypeId is unknown surfaces a clear exception.</summary>
    [Fact]
    public void UnknownTypeIdProducesJsonException()
    {
        var options = BuildOptions();
        var json = "{\"__TypeId\":\"deadbeef-dead-4dea-bdea-deadbeefdead\"}";

        var ex = Assert.Throws<UnresolvableTypeIdException>(() => JsonSerializer.Deserialize<BaseSample>(json, options));
        Assert.Contains("deadbeef-dead-4dea-bdea-deadbeefdead", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A camera's field of view survives a round trip, even from a file that still carries the physical
    /// camera values whose setters would otherwise recompute it.
    /// </summary>
    [Fact]
    public void CameraFieldOfViewSurvivesRoundTrip()
    {
        var options = BuildOptions();
        var json = JsonSerializer.Serialize<Component>(new CameraComponent { FieldOfViewDegrees = 75f }, options);
        var withPhysical = json[..^1] + ",\"FocalLength\":50,\"SensorHeight\":24}";

        var roundTripped = (CameraComponent)JsonSerializer.Deserialize<Component>(withPhysical, options)!;

        Assert.DoesNotContain("FocalLength", json, StringComparison.Ordinal);
        Assert.Equal(75f, roundTripped.FieldOfViewDegrees, 3);
    }

    [TypeId("b0000001-0000-4000-8000-000000000005")]
    internal sealed class AssetHolder : IdClass
    {
        public SampleAsset? Asset { get; set; }
    }
}

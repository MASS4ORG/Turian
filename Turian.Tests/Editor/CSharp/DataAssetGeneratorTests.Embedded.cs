namespace Turian.Tests;

public sealed partial class DataAssetGeneratorTests
{
    /// <summary>Generated member serializers invoke closed interfaces, including explicit implementations and nulls.</summary>
    [Fact]
    public void EmbeddedValuesRoundTripThroughCompiledInterfaces()
    {
        var value = new EmbeddedCarrier { Value = new EmbeddedValue(7) };
        var json = Serializer.Serialize<DataAsset>(value);
        Assert.Equal(7, Serializer.LoadData<EmbeddedCarrier>(json)!.Value!.Number);
        value.Value = null;
        Assert.Null(Serializer.LoadData<EmbeddedCarrier>(Serializer.Serialize<DataAsset>(value))!.Value);
    }

    /// <summary>An explicitly implemented inline field used to verify generated static-interface dispatch.</summary>
    public sealed record EmbeddedValue(int Number) : IEmbeddedAssetValue<EmbeddedValue>
    {
        void IEmbeddedAssetValue<EmbeddedValue>.WriteJson(Utf8JsonWriter writer, JsonSerializerOptions options,
            IdObject owner, string member) => writer.WriteNumberValue(Number);

        static EmbeddedValue IEmbeddedAssetValue<EmbeddedValue>.ReadJson(JsonElement json,
            JsonSerializerOptions options, IdObject owner, string member) => new(json.GetInt32());
    }

    /// <summary>A generated owner with a nullable inline field.</summary>
    [TypeId("f46961d3-cad2-4192-b9ca-72fc16f2adbc")]
    public sealed class EmbeddedCarrier : DataAsset
    {
        /// <summary>The optional inline payload.</summary>
        public EmbeddedValue? Value { get; set; }
    }
}

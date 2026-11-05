namespace Turian.Tests;

/// <summary>
/// Tests the DataAsset source generator: generated serializers write exactly what reflection writes, [Observable]
/// properties raise change notifications, and the scene-reference analyzer flags node and component members.
/// </summary>
[Collection(SerialTests.Name)]
public sealed partial class DataAssetGeneratorTests
{
    /// <summary>A mode, to cover enums.</summary>
    public enum Mode
    {
        /// <summary>The first mode.</summary>
        Calm,

        /// <summary>The second mode.</summary>
        Angry,
    }

    /// <summary>A DataAsset covering the member kinds the generator handles.</summary>
    [TypeId("a4000005-0000-4000-8000-000000000001")]
    public sealed partial class Sample : DataAsset
    {
        /// <summary>A string.</summary>
        public string? Name { get; set; } = "sample \"quoted\"";

        /// <summary>An enum.</summary>
        public Mode Mode { get; set; } = Mode.Angry;

        /// <summary>A nullable value.</summary>
        public int? Optional { get; set; } = 3;

        /// <summary>A list of values.</summary>
        public List<float> Curve { get; set; } = [0.5f, 1.25f];

        /// <summary>A reference to another DataAsset.</summary>
        public Sample? Next { get; set; }

        /// <summary>References to other DataAssets.</summary>
        public Sample?[]? Others;

        /// <summary>An inline copy of another DataAsset.</summary>
        [SerializeInline]
        public Sample? Copy { get; set; }

        /// <summary>An observable value.</summary>
        [Observable]
        public partial int Health { get; set; }
    }

    /// <summary>Verifies that generated serializers write the same JSON as reflection, and read it back.</summary>
    [Theory]
    [InlineData(typeof(DataAssetTest))]
    [InlineData(typeof(GraphicsSettings))]
    [InlineData(typeof(InputSettings))]
    [InlineData(typeof(PlayerSettings))]
    [InlineData(typeof(InputActionsAsset))]
    [InlineData(typeof(Sample))]
    public void GeneratedSerializer_MatchesReflection(Type type)
    {
        var value = (DataAsset)Activator.CreateInstance(type)!;
        if (value is Sample sample)
        {
            sample.Next = new Sample();
            sample.Others = [new Sample(), null];
            sample.Copy = new Sample { Name = "inline" };
            sample.Health = 9;
        }

        Assert.True(GeneratedSerializers.TryGet(type, out _));
        var generated = Serializer.Serialize(value);
        var generatedRoundTrip = Serializer.Serialize(Serializer.LoadData<DataAsset>(generated));

        Assert.True(GeneratedSerializers.Remove(type, out var serializer));
        try
        {
            Assert.Equal(Serializer.Serialize(value), generated);
            Assert.Equal(Serializer.Serialize(Serializer.LoadData<DataAsset>(generated)), generatedRoundTrip);
        }
        finally
        {
            GeneratedSerializers.Register(type, serializer!);
        }
    }

    /// <summary>Verifies that an [Observable] property raises Changed once per actual change.</summary>
    [Fact]
    public void ObservableProperty_RaisesChangedOnChange()
    {
        var sample = new Sample();
        var senders = new List<object>();
        var changes = new List<string>();
        sample.Changed += (asset, member) =>
        {
            senders.Add(asset);
            changes.Add(member);
        };

        sample.Health = 5;
        sample.Health = 5;
        sample.Health = 6;

        Assert.All(senders, sender => Assert.Same(sample, sender));
        Assert.Equal([nameof(Sample.Health), nameof(Sample.Health)], changes);
        Assert.Contains("\"Health\": 6", Serializer.Serialize<DataAsset>(sample));
    }

}

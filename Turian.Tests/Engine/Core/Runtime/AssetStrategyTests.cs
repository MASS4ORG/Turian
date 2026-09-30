namespace Turian.Tests;

/// <summary>Compares designer-wired DataAsset behavior with a separately registered game service.</summary>
public class AssetStrategyTests
{
    [TypeId("b850b4df-a96a-461a-b19a-1a252ecfc241")]
    class CapacityRule : DataAsset
    {
        public int MaxItems { get; set; }

        public virtual bool CanStore(int count) => count < MaxItems;
    }

    [TypeId("297297bc-d3e0-4c63-9d2d-81d37793902c")]
    sealed class BonusRule : CapacityRule
    {
        public override bool CanStore(int count) => count < MaxItems + 2;
    }

    /// <summary>Two authored assets can supply different code without a game service module.</summary>
    [Fact]
    public async Task DesignerSwitchesPolicyWithoutRegisteringAGameService()
    {
        var smallId = Guid.NewGuid();
        var bonusId = Guid.NewGuid();
        var assets = Substitute.For<IAssetLoader>();
        assets.LoadContentAsync<CapacityRule>(smallId)
            .Returns(Task.FromResult<CapacityRule?>(new CapacityRule { MaxItems = 2 }));
        assets.LoadContentAsync<CapacityRule>(bonusId)
            .Returns(Task.FromResult<CapacityRule?>(new BonusRule { MaxItems = 2 }));
        var slot = new DataAssetReference<CapacityRule>(smallId);

        Assert.False((await slot.LoadContentAsync(assets))!.CanStore(3));
        slot.Set(bonusId);
        Assert.True((await slot.LoadContentAsync(assets))!.CanStore(3));
        Assert.Equal(bonusId.ToString(), JsonNode.Parse(Serializer.Serialize(slot))!["AssetId"]!.GetValue<string>());
    }
}

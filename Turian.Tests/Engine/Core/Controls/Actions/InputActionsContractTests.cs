namespace Turian.Tests;

/// <summary>Verifies the session-scoped input-actions contract.</summary>
public class InputActionsContractTests
{
    /// <summary>A consumer sees only the action service registered for its provider.</summary>
    [Fact]
    public void InputActionService_ImplementsTheInjectedReadContract()
    {
        var input = Substitute.For<IInputSource>();
        var service = new InputActionService(input);
        service.Load(InputActionsAsset.CreateDefault());
        IInputActions actions = service;

        Assert.Same(service.Find("Jump"), actions.Find("Jump"));
        Assert.Equal(service.Find("Jump")!.IsPressed, actions.IsPressed("Jump"));
        Assert.Equal(service.Find("Move")!.ReadVector(), actions.ReadVector("Move"));
    }
}

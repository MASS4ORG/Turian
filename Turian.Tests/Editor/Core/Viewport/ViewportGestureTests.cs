namespace Turian.Tests;

/// <summary>Tests for the viewport's pointer-button gesture tracking.</summary>
public class ViewportGestureTests
{
    /// <summary>A press, a held frame and a release report their phases and the owning button.</summary>
    [Fact]
    public void PressDragRelease()
    {
        var gesture = new ViewportGesture();
        var held = new HashSet<ViewportButton>();

        Assert.Equal(ViewportGesturePhase.None, gesture.Update(held.Contains));

        held.Add(ViewportButton.Left);
        Assert.Equal(ViewportGesturePhase.Pressed, gesture.Update(held.Contains));
        Assert.Equal(ViewportButton.Left, gesture.Active);
        Assert.Equal(ViewportGesturePhase.Dragged, gesture.Update(held.Contains));

        held.Clear();
        Assert.Equal(ViewportGesturePhase.Released, gesture.Update(held.Contains));
        Assert.Equal(ViewportButton.Left, gesture.Released);
        Assert.Null(gesture.Active);

        Assert.Equal(ViewportGesturePhase.None, gesture.Update(held.Contains));
        Assert.Null(gesture.Released);
    }

    /// <summary>Right wins over middle and middle over left; the owner alone is polled until it lets go.</summary>
    [Theory]
    [InlineData(new[] { ViewportButton.Left, ViewportButton.Middle }, ViewportButton.Middle)]
    [InlineData(new[] { ViewportButton.Middle, ViewportButton.Right }, ViewportButton.Right)]
    public void OwnerPrecedenceAndExclusivePolling(ViewportButton[] pressed, ViewportButton owner)
    {
        var gesture = new ViewportGesture();
        var polled = new List<ViewportButton>();

        gesture.Update(pressed.Contains);
        Assert.Equal(owner, gesture.Active);

        gesture.Update(button =>
        {
            polled.Add(button);
            return true;
        });
        Assert.Equal(new[] { owner }, polled);
    }

    /// <summary>The camera's button flags follow the gesture's owner.</summary>
    [Theory]
    [InlineData(ViewportButton.Left)]
    [InlineData(ViewportButton.Middle)]
    [InlineData(ViewportButton.Right)]
    [InlineData(null)]
    public void CameraFollowsOwner(ViewportButton? owner)
    {
        var camera = new SceneCameraController(new EditorCamera());

        camera.SetActiveButton(owner);

        Assert.Equal(owner == ViewportButton.Left, camera.IsLeftButton);
        Assert.Equal(owner == ViewportButton.Middle, camera.IsMiddleButton);
        Assert.Equal(owner == ViewportButton.Right, camera.IsRightButton);
    }
}

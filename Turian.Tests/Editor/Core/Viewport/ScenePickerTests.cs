namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="ScenePicker.PickClosest"/> — the pure closest-hit logic, tested against
/// known nodes and world bounds rather than through <see cref="ModelComponent"/> resolution, which
/// needs a GPU (<see cref="ModelComponent.ModelInstance"/>) that headless tests don't have.
/// </summary>
public class ScenePickerTests
{
    static Bounds Box(float centerX) =>
        new(new Vector3(centerX - 1f, -1f, -1f), new Vector3(centerX + 1f, 1f, 1f));

    /// <summary>A ray through one of two candidates selects that candidate's node.</summary>
    [Fact]
    public void PickClosest_RayThroughOneCandidate_SelectsIt()
    {
        var hitNode = new Node { Name = "Hit" };
        var missNode = new Node { Name = "Miss" };
        var candidates = new[] { (hitNode, Box(0f)), (missNode, Box(20f)) };
        var ray = new Ray(new Vector3(0f, 0f, -10f), new Vector3(0f, 0f, 1f));

        Assert.Same(hitNode, ScenePicker.PickClosest(candidates, ray));
    }

    /// <summary>A ray through neither candidate returns null.</summary>
    [Fact]
    public void PickClosest_RayThroughNoCandidate_ReturnsNull()
    {
        var candidates = new[] { (new Node { Name = "A" }, Box(0f)), (new Node { Name = "B" }, Box(20f)) };
        var ray = new Ray(new Vector3(0f, 50f, -10f), new Vector3(0f, 0f, 1f));

        Assert.Null(ScenePicker.PickClosest(candidates, ray));
    }

    /// <summary>Of two overlapping-on-the-ray boxes, the nearer one wins.</summary>
    [Fact]
    public void PickClosest_TwoBoxesOnTheSameRay_PicksTheCloserOne()
    {
        var near = new Node { Name = "Near" };
        var far = new Node { Name = "Far" };
        var candidates = new[] { (far, Box(10f)), (near, Box(0f)) };
        var ray = new Ray(new Vector3(0f, 0f, -10f), new Vector3(0f, 0f, 1f));

        Assert.Same(near, ScenePicker.PickClosest(candidates, ray));
    }

    /// <summary>No candidates at all also returns null rather than throwing.</summary>
    [Fact]
    public void PickClosest_NoCandidates_ReturnsNull()
    {
        var ray = new Ray(Vector3.Zero, new Vector3(0f, 0f, 1f));

        Assert.Null(ScenePicker.PickClosest([], ray));
    }

    /// <summary>
    /// <see cref="ScenePicker.Pick"/> composes <see cref="CameraMath.ScreenPointToRay"/> with the
    /// tested closest-hit logic; a hierarchy with no <see cref="ModelComponent"/> at all — the one
    /// case fully exercisable without a GPU — returns null rather than throwing.
    /// </summary>
    [Fact]
    public void Pick_HierarchyWithNoModelComponents_ReturnsNull()
    {
        var root = new Node { Name = "root" };
        root.Children.Add(new Node { Name = "empty child" });
        var camera = new EditorCamera();

        var hit = ScenePicker.Pick(root, camera, new Vector2(480f, 270f), new Vector2(960f, 540f));

        Assert.Null(hit);
    }
}

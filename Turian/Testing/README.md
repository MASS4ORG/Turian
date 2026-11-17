# MASS4.Turian.Testing

Headless component testing for Turian: `TestScene` runs your components in a real engine scene with controlled frames and scripted input, and `[SceneTest]` methods are discovered and run by `turian-cli test` and Studio's Test Runner.

```csharp
public static class CounterTests
{
    [SceneTest]
    public static void OnePressIsConsumedOnce(TestScene scene)
    {
        var counter = scene.Add<Counter>();
        scene.Input.PushKeyDown(Key.Space);
        scene.Step(3);
        SceneAssert.Equal(1, counter.Presses);
    }
}
```

See the [Component Testing manual](https://turian.mass4.org/docs/manual/debugging/component-testing/) for writing tests, the command line, Studio and CI.

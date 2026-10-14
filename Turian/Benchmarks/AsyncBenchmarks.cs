/// <summary>Measures gameplay scheduler allocation and CPU cost independently of scene traversal.</summary>
static class AsyncBenchmarks
{
    const int Waiters = 10_000;
    const int WarmupFrames = 256;
    const int MeasuredFrames = 1_000;
    const int Samples = 7;

    /// <summary>Runs the allocation gate and reports pending and resumed wait costs.</summary>
    public static void Run()
    {
        Console.WriteLine($"Gameplay async: {Environment.OSVersion}; {RuntimeInformation.FrameworkDescription}; " +
            $"{Environment.ProcessorCount} logical processors");
        Measure("NextFrame", true);
        Measure("Pending seconds", false);
    }

    static void Measure(string label, bool resume)
    {
        using var scheduler = new GameLoopScheduler();
        var routines = new Task[Waiters];
        for (var i = 0; i < Waiters; i++)
        {
            routines[i] = Loop(scheduler, resume);
        }

        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            Pump(scheduler);
        }

        var samples = new double[Samples];
        var allocated = 0L;
        for (var sample = 0; sample < Samples; sample++)
        {
            var start = Stopwatch.GetTimestamp();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < MeasuredFrames; frame++)
            {
                Pump(scheduler);
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            samples[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / (MeasuredFrames * Waiters);
        }
        Array.Sort(samples);
        Console.WriteLine($"{label}: {allocated / (Samples * MeasuredFrames)} B/frame; " +
            $"{samples[Samples / 2]:F1} ns/wait/frame (median of {Samples})");
        scheduler.Dispose();
        Task.WhenAll(routines).GetAwaiter().GetResult();
        if (allocated != 0)
        {
            throw new InvalidOperationException($"{label} allocated {allocated} bytes after warm-up.");
        }
    }

    static async Task Loop(GameLoopScheduler scheduler, bool resume)
    {
        try
        {
            while (true)
            {
                if (resume)
                {
                    await scheduler.NextFrame();
                }
                else
                {
                    await scheduler.Seconds(double.MaxValue);
                }
            }
        }
        catch (OperationCanceledException) when (scheduler.IsStopped) { }
    }

    static void Pump(GameLoopScheduler scheduler)
    {
        scheduler.BeginFrame(1d / 60, 1d / 60);
        scheduler.DrainUpdate();
    }
}

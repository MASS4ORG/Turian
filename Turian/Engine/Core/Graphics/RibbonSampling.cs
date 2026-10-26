namespace Turian.Engine.Core;

static class RibbonSampling
{
    internal static float[] Distances(Vector3[] points)
    {
        var times = new float[points.Length];
        for (var i = 1; i < points.Length; i++)
        {
            times[i] = times[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }

        if (times.Length > 0 && times[^1] > 0)
        {
            var total = times[^1];
            for (var i = 1; i < times.Length; i++)
            {
                times[i] /= total;
            }
        }
        return times;
    }

    internal static (Vector3[] Points, float[] Times) Subdivide(Vector3[] points, float[] times,
        FloatCurve width, ColorGradient color)
    {
        if (points.Length < 2 || times[^1] <= times[0])
        {
            return (points, times);
        }

        var samples = new SortedSet<float>(times);
        AddKeys(samples, width.Keys.Select(static key => key.Time), times[0], times[^1]);
        AddKeys(samples, color.Keys.Select(static key => key.Time), times[0], times[^1]);

        var outputTimes = samples.ToArray();
        return (SamplePositions(points, times, outputTimes), outputTimes);
    }

    static void AddKeys(SortedSet<float> samples, IEnumerable<float> keys, float start, float end)
    {
        foreach (var time in keys)
        {
            if (time > start && time < end)
            {
                samples.Add(time);
            }
        }
    }

    static Vector3[] SamplePositions(Vector3[] points, float[] times, float[] outputTimes)
    {
        var result = new Vector3[outputTimes.Length];
        var segment = 1;
        for (var i = 0; i < outputTimes.Length; i++)
        {
            var t = outputTimes[i];
            while (segment < times.Length - 1 && times[segment] <= t)
            {
                segment++;
            }

            var length = times[segment] - times[segment - 1];
            var fraction = length > 0 ? (t - times[segment - 1]) / length : 0;
            result[i] = Vector3.Lerp(points[segment - 1], points[segment], fraction);
        }
        return result;
    }
}

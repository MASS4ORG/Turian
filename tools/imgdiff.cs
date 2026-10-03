#!/usr/bin/env dotnet
// Compares two images pixel by pixel (RGB) and reports how many differ, the largest channel delta and the bounding
// box of the differences. Exits 0 when the images match within the tolerance, 1 when they differ, 2 on bad input.
//
// Usage: dotnet run tools/imgdiff.cs -- <expected.png> <actual.png> [--tolerance <0-255>] [--out <diff.png>]
//   --tolerance  largest per-channel difference still counted as equal (default: 2)
//   --out        writes a mask: differing pixels white, matching pixels black

#:package SkiaSharp@4.153.1
#:package SkiaSharp.NativeAssets.Linux@4.153.1

using System.Globalization;
using SkiaSharp;

var files = args.Where((arg, i) => !arg.StartsWith("--", StringComparison.Ordinal)
    && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal))).ToList();
if (files.Count != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/imgdiff.cs -- <expected> <actual> [--tolerance n] [--out diff.png]");
    return 2;
}

var tolerance = int.Parse(Option("--tolerance") ?? "2", CultureInfo.InvariantCulture);
using var expected = SKBitmap.Decode(files[0]);
using var actual = SKBitmap.Decode(files[1]);
if (expected is null || actual is null)
{
    Console.Error.WriteLine($"Cannot decode {(expected is null ? files[0] : files[1])}.");
    return 2;
}

if (expected.Width != actual.Width || expected.Height != actual.Height)
{
    Console.WriteLine($"Size differs: {expected.Width}×{expected.Height} vs {actual.Width}×{actual.Height}.");
    return 1;
}

using var mask = new SKBitmap(expected.Width, expected.Height);
var (differing, largest) = (0, 0);
var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, -1, -1);
for (var y = 0; y < expected.Height; y++)
{
    for (var x = 0; x < expected.Width; x++)
    {
        var (a, b) = (expected.GetPixel(x, y), actual.GetPixel(x, y));
        var delta = Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));
        largest = Math.Max(largest, delta);
        var differs = delta > tolerance;
        mask.SetPixel(x, y, differs ? SKColors.White : SKColors.Black);
        if (!differs) continue;

        differing++;
        (left, top) = (Math.Min(left, x), Math.Min(top, y));
        (right, bottom) = (Math.Max(right, x), Math.Max(bottom, y));
    }
}

if (Option("--out") is { } output)
{
    using var stream = File.Create(output);
    mask.Encode(stream, SKEncodedImageFormat.Png, 100);
}

var total = expected.Width * expected.Height;
Console.WriteLine(differing == 0
    ? $"Identical within ±{tolerance} ({total:N0} pixels, largest delta {largest})."
    : $"{differing:N0} of {total:N0} pixels differ (largest delta {largest}), " +
      $"box ({left},{top})-({right},{bottom}).");
return differing == 0 ? 0 : 1;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

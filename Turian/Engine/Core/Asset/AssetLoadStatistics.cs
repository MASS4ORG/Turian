namespace Turian.Engine.Core;

/// <summary>Accumulates cold model and texture load attempts, including decoding and GPU upload.</summary>
public sealed class AssetLoadStatistics
{
    readonly object gate = new();
    int models;
    int textures;
    double modelMilliseconds;
    double textureMilliseconds;

    /// <summary>Returns a consistent snapshot of this database's asset load attempts.</summary>
    public AssetLoadStats Snapshot
    {
        get
        {
            lock (gate) return new(models, textures, modelMilliseconds, textureMilliseconds);
        }
    }

    internal Scope Measure(bool texture) => new(this, texture);

    internal readonly struct Scope(AssetLoadStatistics owner, bool texture) : IDisposable
    {
        readonly long started = Stopwatch.GetTimestamp();

        /// <summary>Records the elapsed cold load attempt in its database.</summary>
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (owner.gate)
            {
                if (texture)
                {
                    owner.textures++;
                    owner.textureMilliseconds += elapsed;
                }
                else
                {
                    owner.models++;
                    owner.modelMilliseconds += elapsed;
                }
            }
        }
    }
}

/// <summary>Cold load attempts and their accumulated wall times; cache hits are excluded.</summary>
/// <param name="Models">Model load attempts.</param>
/// <param name="Textures">Texture load attempts.</param>
/// <param name="ModelMilliseconds">Accumulated model decoding and upload time.</param>
/// <param name="TextureMilliseconds">Accumulated texture decoding and upload time.</param>
public readonly record struct AssetLoadStats(int Models, int Textures,
    double ModelMilliseconds, double TextureMilliseconds);

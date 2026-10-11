namespace Turian.Engine.Core;

/// <summary>CPU elapsed timings and submitted standard geometry for one completed offscreen frame.</summary>
public readonly record struct RenderFrameStats
{
    /// <summary>Wall time of the render call, including submission and its completion wait.</summary>
    public double CpuMilliseconds { get; init; }

    /// <summary>CPU time gathering geometry, resolving materials and uploading frame data.</summary>
    public double PrepareMilliseconds { get; init; }

    /// <summary>CPU time recording and submitting commands, including the completion wait.</summary>
    public double SubmitMilliseconds { get; init; }

    /// <summary>Managed bytes allocated on the rendering thread during the render call.</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Standard geometry draw commands recorded, including indirect commands.</summary>
    public int DrawCalls { get; init; }

    /// <summary>Triangle-list primitives submitted before any GPU occlusion rejection.</summary>
    public long Triangles { get; init; }

    /// <summary>Material descriptor binds recorded for standard geometry.</summary>
    public int MaterialBinds { get; init; }

    /// <summary>Standard submeshes submitted and rejected by CPU frustum culling.</summary>
    public RenderCullingStats Submeshes { get; init; }

    /// <summary>Whether submitted commands can be rejected by the GPU visibility pass.</summary>
    public bool Indirect { get; init; }
}

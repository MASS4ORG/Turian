namespace Gaya.Plugin.Turian;

/// <summary>The Scene viewport's selection mirroring, camera preview and resource release.</summary>
sealed partial class SceneViewport
{
    // ── Selection ───────────────────────────────────────────────────────────

    void OnSelectionChanged()
    {
        if (isActive())
        {
            SyncSelection();
        }
        else
        {
            selectionStale = true;
        }
    }

    void SyncSelection()
    {
        selectionStale = false;
        Gizmo.SelectedNode = inspector.SelectedNode;
        Gizmo.SelectedNodes = inspector.SelectedNodes;
        UpdatePreviewCamera(inspector.SelectedNode?.GetComponent<CameraComponent>());
    }

    void UpdatePreviewCamera(CameraComponent? camera)
    {
        if (camera is null || failure is not null)
        {
            DisposePreview();
            return;
        }

        previewCamera = camera;

        var aspect = camera.AspectRatio > 0.01f ? camera.AspectRatio : 16f / 9f;
        var width = (uint)previewMaxWidth;
        var height = (uint)Math.Max(1, MathF.Round(previewMaxWidth / aspect));

        try
        {
            previewService ??= new SceneViewerService(vulkan, assets, width, height);
            if (previewService.Width != width || previewService.Height != height)
            {
                previewService.Resize(width, height);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The camera preview could not start");
            DisposePreview();
            return;
        }

        if (previewPixels.Length != width * height * 4)
        {
            previewPixels = new byte[width * height * 4];
        }
    }

    void DisposePreview()
    {
        previewCamera = null;
        previewService?.Dispose();
        previewService = null;
        previewFrame?.Dispose();
        previewFrame = null;
        previewPixels = [];
    }

    /// <summary>Whether the viewport currently holds its render target, which it builds on the first frame after a release.</summary>
    internal bool HasRenderTarget => service is not null;

    /// <summary>
    /// Releases the render service, the camera preview and every frame buffer while the document is out of sight, keeping
    /// the camera pose; the next render builds them again.
    /// </summary>
    public void Suspend()
    {
        if (controller is not null)
        {
            releasedPose = ViewportCameraPose.Capture(controller);
        }

        controller = null;
        heldKeys.Clear();
        selectionStale = true;
        ReleaseRenderTargets();
    }

    void ReleaseRenderTargets()
    {
        DisposePreview();
        frame?.Dispose();
        frame = null;
        if (service is not null)
        {
            service.OnPopulateGizmos = null;
        }

        service?.Dispose();
        service = null;
        uiPresenter?.Dispose();
        uiPresenter = null;
        overlayRoot = null;
        pixels = [];
    }
}

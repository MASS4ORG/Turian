namespace Gaya.Host;

/// <summary>
/// Panel instances and documents: a global panel has one instance, a document-bound panel one per open document, built
/// the first time that document is active and released when it closes.
/// </summary>
public sealed partial class Workbench
{
    /// <summary>Identifies a panel instance; the document id is null for a global panel.</summary>
    readonly record struct InstanceKey(string PanelId, string? DocumentId);

    readonly Dictionary<InstanceKey, IPanel> panelInstances = [];
    readonly HashSet<InstanceKey> suspended = [];
    readonly List<Action> afterFrame = [];
    int recentDocumentLimit = 3;
    bool rendering;
    IDocument? frameDocument;
    LayoutSlot frameSlot;

    /// <summary>
    /// How many of the most recently active documents keep their panels' resources; panels of the others are asked to
    /// suspend. At least one, and three by default.
    /// </summary>
    public int RecentDocumentLimit
    {
        get => recentDocumentLimit;
        set
        {
            recentDocumentLimit = Math.Max(1, value);
            ApplyOrDefer(ApplySuspendPolicy);
        }
    }

    /// <summary>Follows the document service: layout swaps, suspension and release are driven by its events.</summary>
    void SubscribeDocuments()
    {
        app.Documents.ActivationSwitched += OnActivationSwitched;
        app.Documents.ClosingDocument += OnDocumentClosing;
    }

    void OnActivationSwitched(IDocument? document)
    {
        var next = SlotFor(document?.Kind);
        if (!ReferenceEquals(next, activeSlot))
        {
            SaveLayoutIfChanged();
            activeSlot = next;
        }

        if (document is not null)
        {
            suspended.RemoveWhere(key => key.DocumentId == document.Id);
        }

        ApplyOrDefer(ApplySuspendPolicy);
    }

    void OnDocumentClosing(IDocument document) => ApplyOrDefer(() => ReleaseDocument(document.Id));

    /// <summary>Runs now, or after the current frame's second pass when a panel may still be drawing.</summary>
    void ApplyOrDefer(Action action)
    {
        if (rendering)
        {
            afterFrame.Add(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>Runs what was deferred while the frame was drawing.</summary>
    void RunAfterFrame()
    {
        var pending = afterFrame.ToArray();
        afterFrame.Clear();
        foreach (var action in pending)
        {
            action();
        }
    }

    /// <summary>Disposes the instances a closed document's panels had.</summary>
    void ReleaseDocument(string documentId)
    {
        foreach (var key in panelInstances.Keys.Where(key => key.DocumentId == documentId).ToList())
        {
            Release(key);
        }

        closeRequests.RemoveWhere(key => key.DocumentId == documentId);
    }

    /// <summary>Disposes every instance of a panel, whatever document it was bound to.</summary>
    void ReleasePanel(string panelId)
    {
        foreach (var key in panelInstances.Keys.Where(key => key.PanelId == panelId).ToList())
        {
            Release(key);
        }
    }

    void Release(InstanceKey key)
    {
        suspended.Remove(key);
        if (panelInstances.Remove(key, out var panel) && panel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Asks the suspendable panels of every document outside the most recently active ones to release their resources,
    /// once each until the document is active again.
    /// </summary>
    void ApplySuspendPolicy()
    {
        var recent = app.Documents.Recent;
        for (var index = recentDocumentLimit; index < recent.Count; index++)
        {
            var documentId = recent[index].Id;
            foreach (var key in panelInstances.Keys.Where(key => key.DocumentId == documentId).ToList())
            {
                if (panelInstances[key] is not ISuspendablePanel panel || !suspended.Add(key))
                {
                    continue;
                }

                try
                {
                    panel.Suspend();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Panel {PanelId} threw while suspending", key.PanelId);
                }
            }
        }
    }

    bool IsOpen(IDocument document) => app.Documents.Documents.Contains(document);

    static InstanceKey KeyFor(PanelDescriptor descriptor, IDocument? document) =>
        new(descriptor.Id, descriptor.IsDocumentBound ? document?.Id : null);

    /// <summary>The instance of a panel for a document if one was already built, without building it.</summary>
    IPanel? ExistingInstance(string panelId, IDocument? document) =>
        descriptors.TryGetValue(panelId, out var descriptor)
            ? panelInstances.GetValueOrDefault(KeyFor(descriptor, document))
            : null;

    /// <summary>The panel's instance for a document, built on first use; global panels ignore the document.</summary>
    IPanel Resolve(PanelDescriptor descriptor, IDocument? document)
    {
        var key = KeyFor(descriptor, document);
        if (panelInstances.TryGetValue(key, out var panel))
        {
            return panel;
        }

        try
        {
            panel = descriptor.IsDocumentBound
                ? descriptor.DocumentFactory!(app.Services, document!)
                : descriptor.Factory(app.Services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Panel {PanelId} could not be created", descriptor.Id);
            panel = new BrokenPanel(descriptor.Id);
        }

        panelInstances[key] = panel;
        return panel;
    }

    /// <summary>Stands in for a panel whose factory threw, so one bad plugin cannot close the editor.</summary>
    sealed class BrokenPanel(string panelId) : IPanel
    {
        public void Render(Gui gui) =>
            gui.DrawText($"{panelId} failed to load — see the log.", ThemeTokens.Current.Text(12),
                ThemeTokens.Current.Error);
    }

    /// <summary>
    /// Draws one panel's content for the frame's document. Document panels are scoped by the document's id, so two
    /// documents of one kind never share Guinevere's per-node state such as scrolling, text input or tree expansion.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="descriptor">The panel to draw.</param>
    /// <param name="scope">Names what is drawn, so a panel's body and its header never share an item key.</param>
    /// <param name="draw">Draws the resolved instance.</param>
    void InDocumentScope(Gui gui, PanelDescriptor descriptor, string scope, Action<IPanel> draw)
    {
        if (!descriptor.AllowedIn(frameSlot.Kind))
        {
            return;
        }

        if (!descriptor.IsDocumentBound)
        {
            draw(Resolve(descriptor, null));
            return;
        }

        if (frameDocument is not { } document || !IsOpen(document))
        {
            return;
        }

        using (gui.ItemKey((scope, document.Id)))
        {
            draw(Resolve(descriptor, document));
        }
    }
}

namespace Turian.Editor.Core;

/// <summary>
/// Stateful controller that owns the current selection and exposes
/// component CRUD operations. No UI dependencies.
/// </summary>
public sealed class NodeInspectorController
{
    readonly AssetManager assetManager;

    /// <summary>Gets the currently selected Node, or null if no node is selected.</summary>
    public Node? SelectedNode => selectedNode;

    /// <summary>
    /// The object handed to <see cref="Select"/>, whatever its type. A shell that can edit
    /// project-level objects — the app settings, say — reads this when
    /// <see cref="SelectedNode"/> is null.
    /// </summary>
    public object? SelectedObject { get; private set; }

    Node? selectedNode;

    /// <summary>Raised after selection or component list changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="NodeInspectorController"/> class.
    /// </summary>
    /// <param name="assetManager">The asset manager used for persistence operations.</param>
    public NodeInspectorController(AssetManager assetManager)
    {
        this.assetManager = assetManager;
    }

    /// <summary>
    /// Returns an <see cref="Action{T}"/> that marks the engine dirty after
    /// a member mutation. Pass this to <see cref="MemberValueAccessor.SetValue"/>
    /// or <see cref="MemberValueAccessor.TrySetValue"/> from the UI layer.
    /// </summary>
    /// <returns>An action that notifies the system of mutations to the specified target.</returns>
    public Action<object> CreateMutationNotifier() => NotifyMutation;

    /// <summary>
    /// Selects the specified target object, updating the current selection state.
    /// </summary>
    /// <param name="target">The object to select. Can be a Node, Component, or null to clear selection.</param>
    public void Select(object? target)
    {
        SelectedObject = target;
        selectedNode = target switch
        {
            Node n => n,
            Component { IsAttached: true } c => c.Node,
            _ => null
        };
        SelectionChanged?.Invoke();
    }

    /// <summary>Clears the current selection, setting both SelectedObject and SelectedNode to null.</summary>
    public void ClearSelection() => Select(null);

    /// <summary>
    /// Instantiates and attaches <paramref name="componentType"/> to <see cref="selectedNode"/>.
    /// </summary>
    /// <param name="componentType">The type of component to add to the selected node.</param>
    /// <returns>True if the component was successfully added; otherwise, false.</returns>
    public bool AddComponent(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (selectedNode is null) return false;
        if (!typeof(Component).IsAssignableFrom(componentType)) return false;
        if (!ComponentRegistry.CanAddTo(selectedNode, componentType)) return false;

        try
        {
            if (Activator.CreateInstance(componentType) is not Component instance) return false;
            selectedNode.AddComponent(instance);
            NotifyNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to add component {Type}", componentType.FullName);
            return false;
        }
    }

    /// <summary>
    /// Removes the specified component from the selected node.
    /// </summary>
    /// <param name="component">The component to remove from the selected node.</param>
    /// <returns>True if the component was successfully removed; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the component is null.</exception>
    public bool RemoveComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (selectedNode is null) return false;

        try
        {
            if (!selectedNode.RemoveComponent(component)) return false;
            NotifyNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to remove component {Type}", component.GetType().FullName);
            return false;
        }
    }

    /// <summary>
    /// Moves the specified component up one position in the component list.
    /// </summary>
    /// <param name="component">The component to move up.</param>
    public void MoveComponentUp(Component component)
    {
        if (selectedNode is null) return;
        var idx = selectedNode.Components.IndexOf(component);
        if (idx <= 0) return;
        selectedNode.Components.RemoveAt(idx);
        selectedNode.Components.Insert(idx - 1, component);
        NotifyNodeChanged();
    }

    /// <summary>
    /// Moves the specified component down one position in the component list.
    /// </summary>
    /// <param name="component">The component to move down.</param>
    public void MoveComponentDown(Component component)
    {
        if (selectedNode is null) return;
        var idx = selectedNode.Components.IndexOf(component);
        if (idx < 0 || idx >= selectedNode.Components.Count - 1) return;
        selectedNode.Components.RemoveAt(idx);
        selectedNode.Components.Insert(idx + 1, component);
        NotifyNodeChanged();
    }

    /// <summary>
    /// Returns filtered component types addable to the current node.
    /// Safe to call off the UI thread.
    /// </summary>
    /// <param name="searchText">Optional text to filter components by name or menu path.</param>
    /// <returns>An enumerable collection of component type descriptors matching the search criteria.</returns>
    public IEnumerable<ComponentTypeDescriptor> GetAvailableComponents(string? searchText = null)
    {
        if (selectedNode is null) return [];
        var node = selectedNode;
        var filter = searchText?.Trim() ?? string.Empty;

        return ComponentRegistry.GetAvailableTypes()
            .Where(t => ComponentRegistry.MatchesSearch(t, filter))
            .Where(t => ComponentRegistry.CanAddTo(node, t))
            .Select(t => new ComponentTypeDescriptor(t))
            .OrderBy(d => d.MenuPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Notifies the asset manager of mutations to the specified target.
    /// </summary>
    /// <param name="target">The target object that was mutated (Node or Component).</param>
    void NotifyMutation(object target)
    {
        var node = target switch
        {
            Node n => n,
            Component { IsAttached: true } c => c.Node,
            _ => null
        };
        if (node is not null)
        {
            assetManager.RefreshNode(node);
            assetManager.UpdateSelectedNode();
        }

        assetManager.AlterAssetIfOpened();
    }

    /// <summary>
    /// Notifies the asset manager that the selected node has changed and raises the SelectionChanged event.
    /// </summary>
    void NotifyNodeChanged()
    {
        if (selectedNode is not null)
        {
            assetManager.RefreshNode(selectedNode);
            assetManager.UpdateSelectedNode();
        }

        assetManager.AlterAssetIfOpened();
        SelectionChanged?.Invoke();
    }
}

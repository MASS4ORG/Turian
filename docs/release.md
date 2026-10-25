# Migration: node children (#64, ADR-022 next breaking batch)

Apply this migration to all five production games and their bricks together. Rebuild game code and precompiled bricks against the new engine. `Node.Children` changes from `ObservableCollection<Node>` to public `NodeCollection`; Vulkan `FrameInfo.Nodes` uses the same type. Scene/prefab JSON remains an ordered `Children` array and needs no format conversion. This change is scheduled for the next breaking batch, not a compatible patch.

## Find and replace

Limit searches to declarations and subscriptions for **node children**; tags and other observable collections keep their types.

| Find | Replace with |
| --- | --- |
| `ObservableCollection<Node>` / `new ObservableCollection<Node>()` | `NodeCollection` / `new NodeCollection()`; read-only consumers can use `IReadOnlyList<Node>` |
| `parent.Children = savedObservableCollection` | `parent.Children.ReplaceWith(savedObservableCollection)` |
| `children.CollectionChanged += handler` | `children.StructureChanged += handler`; change the handler's argument type to `EventArgs` and refresh from current children |
| Child-list `PropertyChanged` or action/item-diff handlers | Poll `Version`, or refresh once on `StructureChanged`; there are no item payloads or property notifications |
| Repeated references / listings under several parents | Use separate node instances; use `Move` to reorder and `Add` to reparent |

Collection expressions (`Children = [a, b]`), `Add`, `Insert`, `Move`, `RemoveAt`, `Remove` and `Clear` remain available. Assignment copies into the node's existing collection; an unowned `NodeCollection` is a builder and is not the live storage. Retained children keep tag registrations and transform stamps when reordered.

## Membership and indexed operations

An owned child appears once under one parent. `Add(existing)` moves it to the end; adding to another parent removes the old listing. Assigned duplicates retain their first occurrence. `child.Parent = parent` attaches and `Parent = null` detaches; assigning the current parent is a no-op. `Contains`, `IndexOf` and `Remove` use reference identity, so another object with the same `Id` does not match.

`Count` counts live children. `Children[i]`, `IndexOf`, `Insert`, `Move` and `RemoveAt` use **logical positions among live children**, never storage slots. Indexed operations compact interior holes on demand without a version/event change. `Insert(index, existing)` counts the destination after removing the existing listing: move to the end with index `Count - 1`. `Move(oldIndex, newIndex)` uses the current logical positions. Replacing an indexed child with an already listed child moves that reference and removes the replaced child. Indexed edits can shift siblings; use `Remove(child)` for amortized constant-time removal.

## Notifications and iteration

`Version` changes on structural edits; static `Node.HierarchyVersion` tracks edits across owned collections. Compare stamps for inequality; one operation can advance them more than once. They do not describe names, transforms or components. `StructureChanged` sends `EventArgs.Empty` after a completed operation, coalescing replacement edits and each affected parent's destruction flush into one notification. Reparent notifications see both collections updated. Re-read the collection rather than expecting an add/remove diff; unsubscribe when the observer is disposed.

```csharp
void RefreshChildren(object? sender, EventArgs args) => RefreshTree(parent.Children);
parent.Children.StructureChanged += RefreshChildren;
// On observer disposal:
parent.Children.StructureChanged -= RefreshChildren;
```

Direct `foreach` uses a struct enumerator, preserves sibling order and skips tombstones. Structural edits invalidate it; enumerate a snapshot when mutating. Read-triggered compaction preserves enumeration.

**Deletion timing is unchanged.** As with Unity's contract, `Destroy()` is deferred and `DestroyImmediate()` is synchronous. Turian additionally flushes at each fixed-tick boundary for ADR-018 replay stability; update requests flush at frame end. OnDestroy callbacks can add requests to the same flush. The breaking change is the children storage/API contract, not this timing.

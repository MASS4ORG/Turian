namespace Turian.Editor.Core;

public sealed partial class UndoService
{
    // Whether the last flush took a change back, so a drag that goes on for many frames is reported once.
    bool tookBack;

    // A read-only document takes no edits, so what changed goes back to how it was.
    void TakeBack(Guid document, Dictionary<IdObject, ObjectState> before, bool report)
    {
        Run(() =>
        {
            foreach (var (target, state) in before)
            {
                state.Restore(target);
            }
        });
        RefreshScene(new UndoStep(string.Empty, document, before, before));
        altered = false;
        tookBack = true;
        WatchSelection();
        if (report)
        {
            Log.Logger.LogWarning("The change was taken back: the document is read-only");
        }

        Changed?.Invoke();
    }
}

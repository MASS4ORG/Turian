namespace Gaya.Plugin.Turian;

sealed class BackgroundTaskUiBlocker(BackgroundTaskManager tasks) : IUiBlocker
{
    public bool IsBlocked => tasks.IsUiBlocked;

    public string Message => tasks.BlockingTask?.Label ?? "Working...";
}

namespace Turian.Editor.Core;

/// <summary>Withdraws one contribution from a live registry the first time it is disposed.</summary>
/// <param name="withdraw">Removes the contribution.</param>
sealed class ContributionLifetime(Action withdraw) : IDisposable
{
    Action? pending = withdraw;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
}

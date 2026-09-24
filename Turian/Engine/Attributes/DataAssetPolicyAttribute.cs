namespace Turian;

/// <summary>
/// How a <c>DataAsset</c> type's runtime-cached content is expected to be written and what
/// happens to it when Play Mode ends. See <c>IAssetLoader.LoadDataAsync</c>, which is the entry
/// point that makes this policy apply: content read through <c>DataAssetAsset.GetContent</c>
/// directly is never cached and is unaffected by this attribute.
/// </summary>
public enum DataAssetPolicy
{
    /// <summary>
    /// Authored data: not expected to be written at runtime. Its cached content is discarded when
    /// Play Mode ends, so a script that mutates it anyway never leaves a trace once play stops.
    /// This is the default for a type with no <see cref="DataAssetPolicyAttribute"/>.
    /// </summary>
    Authored,

    /// <summary>
    /// Runtime-mutable shared state — a SOAP variable, a runtime set. Mutations are visible to
    /// every reader for the rest of the session, but are discarded, the same as <see cref="Authored"/>,
    /// once Play Mode ends: the next session starts from the authored values again.
    /// </summary>
    ResetOnPlay,

    /// <summary>
    /// Runtime-mutable state meant to outlive a single Play Mode session. Its cached content is
    /// kept across Play Mode stop/start. Actually persisting it to disk between game runs is the
    /// save system's job (issue #87), not this attribute's.
    /// </summary>
    Persistent,
}

/// <summary>
/// Declares the <see cref="DataAssetPolicy"/> a <c>DataAsset</c> subclass's cached runtime content
/// follows. Apply to the class; see <see cref="DataAssetPolicy"/> for what each value means.
/// </summary>
/// <param name="policy">The policy this type's cached content follows.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class DataAssetPolicyAttribute(DataAssetPolicy policy) : Attribute
{
    /// <summary>The declared policy.</summary>
    public DataAssetPolicy Policy { get; } = policy;

    /// <summary>
    /// Resolves the effective policy for a type: its own <see cref="DataAssetPolicyAttribute"/>,
    /// or <see cref="DataAssetPolicy.Authored"/> when it declares none.
    /// </summary>
    /// <param name="dataAssetType">A type deriving from <c>DataAsset</c>.</param>
    public static DataAssetPolicy Resolve(Type dataAssetType)
    {
        ArgumentNullException.ThrowIfNull(dataAssetType);
        return dataAssetType.GetCustomAttribute<DataAssetPolicyAttribute>(inherit: true)?.Policy
            ?? DataAssetPolicy.Authored;
    }
}

namespace Turian.Engine.Core;

/// <summary>
/// Base for all engine objects.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000001")]
public class IdClass : IEquatable<IdClass>
{
    /// <summary>
    /// Object ID.
    /// </summary>
    [HideInEditor]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Reference ids read from data whose targets are not resolved yet, by member name.</summary>
    internal List<KeyValuePair<string, Guid[]>>? PendingReferences;

    /// <summary>
    /// Indicates whether the current object is equal to another object of the same type.
    /// </summary>
    /// <param name="other">An object to compare with this object.</param>
    /// <returns>true if the current object is equal to the other parameter; otherwise, false.</returns>
    public bool Equals(IdClass? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Id == other.Id;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current object.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
    public override bool Equals(object? obj) => obj switch
    {
        IdClass other => Equals(other),
        _ => false,
    };

    /// <summary>
    /// Returns a hash code for the current object.
    /// </summary>
    /// <returns>A hash code for the current object.</returns>
    // IDs are deserialized after construction, so they must remain mutable.
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>
    /// Equality operator. Checks if two IdClass instances are equal.
    /// </summary>
    public static bool operator ==(IdClass? left, IdClass? right)
    {
        if (left is null)
        {
            return right is null;
        }
        return left.Equals(right);
    }

    /// <summary>
    /// Inequality operator. Checks if two IdClass instances are not equal.
    /// </summary>
    public static bool operator !=(IdClass? left, IdClass? right) => !(left == right);
}

namespace Homeostat.Conditions;

/// <summary>Lifecycle of a condition.</summary>
public enum ConditionStatus
{
    /// <summary>No triggering signal observed.</summary>
    Inactive = 0,

    /// <summary>Triggering signals observed, but not yet for the required duration.</summary>
    Pending = 1,

    /// <summary>The condition holds and its healing plan may run.</summary>
    Active = 2,
}

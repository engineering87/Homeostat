namespace Homeostat.Conditions;

/// <summary>How a composite condition combines the conditions it depends on.</summary>
internal enum CompositeMode
{
    /// <summary>Active while every listed condition is active.</summary>
    All,

    /// <summary>Active while at least one listed condition is active.</summary>
    Any,
}

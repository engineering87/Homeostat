namespace Homeostat.Metrics;

/// <summary>Instrument kinds that need special handling to give meaningful statistics.</summary>
internal enum InstrumentKind
{
    /// <summary>Counters, histograms and gauges: each measurement is a value of the interval.</summary>
    Other,

    /// <summary>Each measurement is a change of a level; <c>Last</c> reports the level.</summary>
    UpDownCounter,

    /// <summary>Each observation is a running total per series; it is turned into increments.</summary>
    ObservableCounter,

    /// <summary>Each observation is the level of a series; <c>Last</c> reports the sum of the latest levels.</summary>
    ObservableUpDownCounter,
}

using System.Text.Json;
using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>
/// Learned mean and spread of a metric for each time slot of a cycle. The values of a slot in the current period are
/// accumulated; when the slot comes round again in a later period, they are folded into the baseline with an
/// exponential weight, so the baseline follows slow changes and forgets old ones. The current value is compared only
/// with earlier periods.
/// </summary>
/// <remarks>Fed by the engine loop and read by the baseline keeper: every member takes the lock.</remarks>
internal sealed class SeasonalBaseline
{
    /// <summary>Weight of a new period once the baseline has a few: about the last 4 to 7 periods count.</summary>
    internal const double Alpha = 0.25;

    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private long _changes;

    public SeasonalBaseline(string key, Seasonality seasonality)
    {
        Key = key;
        Seasonality = seasonality;
        _slots = new Slot[seasonality.Buckets];
        for (var i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot();
        }
    }

    /// <summary>Key of the persisted baseline: the condition and the cycle.</summary>
    public string Key { get; }

    public Seasonality Seasonality { get; }

    /// <summary>Incremented at every change, for the keeper to know what to save.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>Whether the keeper has loaded the persisted baseline (or found none).</summary>
    public bool Loaded { get; set; }

    public void Add(Signal signal)
    {
        if (signal.Value is not { } value || !double.IsFinite(value))
        {
            return;
        }

        var (bucket, period) = Seasonality.Locate(signal.Timestamp);
        lock (_gate)
        {
            var slot = _slots[bucket];
            if (slot.Count > 0 && period > slot.CurrentPeriod)
            {
                slot.Fold();
            }

            if (slot.Count == 0)
            {
                slot.CurrentPeriod = period;
            }

            slot.Count++;
            slot.Sum += value;
            slot.SumOfSquares += value * value;
            Interlocked.Increment(ref _changes);
        }
    }

    /// <summary>
    /// How many standard deviations the value is from the baseline of its time slot; <c>null</c> until the slot has
    /// <paramref name="minimumPeriods"/> periods of history. The deviation is floored at 1% of the mean, as for the
    /// other anomaly rules.
    /// </summary>
    public double? ZScore(Signal signal, int minimumPeriods)
    {
        if (signal.Value is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        var (bucket, _) = Seasonality.Locate(signal.Timestamp);
        lock (_gate)
        {
            var slot = _slots[bucket];
            if (slot.Periods < minimumPeriods)
            {
                return null;
            }

            var variance = Math.Max(0, slot.MeanOfSquares - (slot.Mean * slot.Mean));
            var deviation = Math.Max(Math.Sqrt(variance), Math.Max(Math.Abs(slot.Mean) * 0.01, 1e-9));
            return (value - slot.Mean) / deviation;
        }
    }

    /// <summary>Periods learned by the slot of an instant, for diagnostics and tests.</summary>
    public int PeriodsAt(DateTimeOffset at)
    {
        lock (_gate)
        {
            return _slots[Seasonality.Locate(at).Bucket].Periods;
        }
    }

    public string Export()
    {
        lock (_gate)
        {
            var state = new State(1, Seasonality.Signature, _slots.Select(s => new SlotState(
                s.Mean, s.MeanOfSquares, s.Periods, s.CurrentPeriod, s.Count, s.Sum, s.SumOfSquares)).ToArray());
            return JsonSerializer.Serialize(state, JsonOptions);
        }
    }

    /// <summary>
    /// Merges a persisted baseline: each slot takes the persisted statistics when they cover at least as many periods
    /// as the slot in memory (which only has data when the baseline is loaded late), and keeps the values that both
    /// accumulated for the current period. Returns <c>false</c> for a document of another cycle or version.
    /// </summary>
    public bool Import(string document)
    {
        State? state;
        try
        {
            state = JsonSerializer.Deserialize<State>(document, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (state is null || state.Version != 1 || state.Signature != Seasonality.Signature || state.Slots is null || state.Slots.Length != _slots.Length)
        {
            return false;
        }

        lock (_gate)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var saved = state.Slots[i];
                if (saved.Periods < 0 || saved.Count < 0 || !double.IsFinite(saved.Mean) || !double.IsFinite(saved.MeanOfSquares)
                    || !double.IsFinite(saved.Sum) || !double.IsFinite(saved.SumOfSquares))
                {
                    continue;
                }

                var slot = _slots[i];
                if (saved.Periods < slot.Periods)
                {
                    continue;
                }

                var merged = new Slot
                {
                    Mean = saved.Mean,
                    MeanOfSquares = saved.MeanOfSquares,
                    Periods = saved.Periods,
                    CurrentPeriod = saved.CurrentPeriod,
                    Count = saved.Count,
                    Sum = saved.Sum,
                    SumOfSquares = saved.SumOfSquares,
                };

                if (slot.Count > 0)
                {
                    // Values seen in memory since the start: fold the saved partial period if it is older, then add them.
                    if (merged.Count > 0 && merged.CurrentPeriod < slot.CurrentPeriod)
                    {
                        merged.Fold();
                    }

                    if (merged.Count == 0 || merged.CurrentPeriod == slot.CurrentPeriod)
                    {
                        merged.CurrentPeriod = slot.CurrentPeriod;
                        merged.Count += slot.Count;
                        merged.Sum += slot.Sum;
                        merged.SumOfSquares += slot.SumOfSquares;
                    }
                }

                _slots[i] = merged;
            }

            Interlocked.Increment(ref _changes);
        }

        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Slot
    {
        public double Mean;
        public double MeanOfSquares;
        public int Periods;
        public long CurrentPeriod;
        public int Count;
        public double Sum;
        public double SumOfSquares;

        /// <summary>Folds the accumulated period into the baseline: equal weights for the first periods, then <see cref="Alpha"/>.</summary>
        public void Fold()
        {
            var mean = Sum / Count;
            var meanOfSquares = SumOfSquares / Count;
            var weight = Math.Max(Alpha, 1.0 / (Periods + 1));
            Mean += weight * (mean - Mean);
            MeanOfSquares += weight * (meanOfSquares - MeanOfSquares);
            Periods++;
            (Count, Sum, SumOfSquares) = (0, 0, 0);
        }
    }

    private sealed record State(int Version, string Signature, SlotState[]? Slots);

    private sealed record SlotState(double Mean, double MeanOfSquares, int Periods, long CurrentPeriod, int Count, double Sum, double SumOfSquares);
}

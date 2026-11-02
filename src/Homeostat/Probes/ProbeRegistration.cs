namespace Homeostat.Probes;

/// <summary>A probe registered with <c>AddProbe</c>: the function that measures and its settings.</summary>
internal sealed class ProbeRegistration(
    string source,
    string origin,
    Func<IServiceProvider, CancellationToken, Task<double>> measure,
    ProbeOptions options)
{
    public string Source { get; } = source;

    /// <summary>Value of the <c>origin</c> tag of published signals.</summary>
    public string Origin { get; } = origin;

    public Func<IServiceProvider, CancellationToken, Task<double>> Measure { get; } = measure;

    public ProbeOptions Options { get; } = options;
}

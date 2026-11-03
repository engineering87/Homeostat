using System.Net;

namespace Homeostat.Testing;

/// <summary>A fault that tests turn on and off.</summary>
public sealed class FaultSwitch
{
    private volatile bool _active;

    /// <summary>Whether the fault is currently injected.</summary>
    public bool IsActive => _active;

    /// <summary>Starts injecting the fault.</summary>
    public void Activate() => _active = true;

    /// <summary>Stops injecting the fault.</summary>
    public void Deactivate() => _active = false;
}

/// <summary>
/// HTTP handler that answers with an error status while its <see cref="FaultSwitch"/> is active, and
/// forwards requests otherwise. Use it to simulate an unreachable dependency.
/// </summary>
/// <example>
/// <code>
/// var fault = new FaultSwitch();
/// services.AddHttpClient("forecast")
///     .AddHomeostatSignals()
///     .AddHttpMessageHandler(() => new FaultInjectingHandler(fault));
/// fault.Activate();
/// </code>
/// </example>
public sealed class FaultInjectingHandler : DelegatingHandler
{
    private readonly FaultSwitch _fault;
    private readonly HttpStatusCode _statusCode;

    /// <summary>Creates the handler.</summary>
    /// <param name="fault">Controls when the fault is active.</param>
    /// <param name="statusCode">Status returned while the fault is active.</param>
    public FaultInjectingHandler(FaultSwitch fault, HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable)
    {
        ArgumentNullException.ThrowIfNull(fault);
        _fault = fault;
        _statusCode = statusCode;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _fault.IsActive
            ? Task.FromResult(new HttpResponseMessage(_statusCode) { RequestMessage = request })
            : base.SendAsync(request, cancellationToken);
}

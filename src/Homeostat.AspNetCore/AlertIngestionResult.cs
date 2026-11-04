namespace Homeostat.AspNetCore;

/// <summary>Response of the alert endpoints.</summary>
/// <param name="Accepted">Alerts published as signals.</param>
/// <param name="Skipped">Alerts ignored: without a source, malformed, over the per-request limit, or published while Homeostat was stopping.</param>
internal sealed record AlertIngestionResult(int Accepted, int Skipped);

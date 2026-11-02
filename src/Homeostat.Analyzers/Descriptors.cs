using Microsoft.CodeAnalysis;

namespace Homeostat.Analyzers;

internal static class Descriptors
{
    private const string Category = "Homeostat";
    private const string HelpBase = "https://github.com/engineering87/Homeostat/blob/main/docs/analyzers.md#";

    public static readonly DiagnosticDescriptor TaskNotAwaited = new(
        "HOMEO001",
        "Inline action returns a task that is never awaited",
        "The lambda returns a '{0}' through the Action<IServiceProvider> overload, so the step completes before the work does and failures are lost; write 'async sp => await ...'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "homeo001");

    public static readonly DiagnosticDescriptor ConditionWithoutSource = new(
        "HOMEO002",
        "Condition does not select its signals",
        "Condition '{0}' has no ForSource, Where or composite rule, so it reacts to every signal from every source",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "homeo002");

    public static readonly DiagnosticDescriptor CancellationIgnored = new(
        "HOMEO003",
        "Healing action ignores its cancellation token",
        "The action awaits work without using '{0}', so the action timeout and application shutdown cannot stop it; pass the token on, or name it '_' if the work cannot be canceled",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "homeo003");

    public static readonly DiagnosticDescriptor AnonymousEndpoints = new(
        "HOMEO004",
        "Homeostat endpoints are mapped without authorization",
        "'{0}' is mapped with allowAnonymous: true, so anyone who can reach it can see internals or change what the application does; use an authorization policy outside development",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "homeo004");

    public static readonly DiagnosticDescriptor PlainBackgroundServiceRestart = new(
        "HOMEO005",
        "Restarting a plain BackgroundService stops the application",
        "'{0}' is a BackgroundService: stopping it outside a shutdown makes the generic host stop the application, so Homeostat refuses the restart; derive it from RestartableBackgroundService",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: HelpBase + "homeo005");
}

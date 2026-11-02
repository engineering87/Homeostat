using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Homeostat.OpenFeature;

/// <summary>
/// OpenFeature provider that exposes Homeostat switches as boolean flags: a flag is <c>true</c> while the switch with
/// the same name (after <see cref="KeyPrefix"/>) is on. Healing plans turn switches on with <c>TurnOnSwitch</c>, so code
/// that already reads flags through OpenFeature degrades without knowing about Homeostat. Keys without the prefix
/// resolve to the default value with <c>FLAG_NOT_FOUND</c>, whatever their type, which lets a multi-provider fall
/// through to your main flag provider; a prefixed key read as another type gets <c>TYPE_MISMATCH</c>. With no prefix
/// every key is a switch, so put the provider after your main one in a multi-provider. A configuration-changed event is emitted whenever a switch changes.
/// </summary>
/// <example>
/// <code>
/// await Api.Instance.SetProviderAsync(new HomeostatSwitchProvider(app.Services.GetRequiredService&lt;IHomeostatSwitches&gt;()));
/// var client = Api.Instance.GetClient();
/// if (await client.GetBooleanValueAsync("serve-cached-forecast", false)) { /* degraded path */ }
/// </code>
/// </example>
public sealed class HomeostatSwitchProvider : FeatureProvider
{
    private static readonly Metadata ProviderMetadata = new("Homeostat switches");
    private readonly IHomeostatSwitches _switches;
    private int _subscribed;

    /// <summary>Creates the provider.</summary>
    /// <param name="switches">Homeostat switches.</param>
    /// <param name="keyPrefix">Prefix that flag keys must carry, removed to get the switch name, for example <c>"homeostat."</c>. Defaults to none.</param>
    public HomeostatSwitchProvider(IHomeostatSwitches switches, string keyPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _switches = switches;
        KeyPrefix = keyPrefix;
    }

    /// <summary>Prefix that flag keys must carry.</summary>
    public string KeyPrefix { get; }

    /// <inheritdoc />
    public override Metadata? GetMetadata() => ProviderMetadata;

    /// <inheritdoc />
    public override Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _switches.Changed += OnSwitchChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            _switches.Changed -= OnSwitchChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetSwitch(flagKey, out var name))
        {
            return Task.FromResult(new ResolutionDetails<bool>(
                flagKey, defaultValue, ErrorType.FlagNotFound, Reason.Error, errorMessage: $"'{flagKey}' does not start with '{KeyPrefix}'."));
        }

        var on = _switches.IsOn(name);
        return Task.FromResult(new ResolutionDetails<bool>(flagKey, on, reason: Reason.Static, variant: on ? "on" : "off"));
    }

    /// <inheritdoc />
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(NotBoolean(flagKey, defaultValue));

    /// <inheritdoc />
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(NotBoolean(flagKey, defaultValue));

    /// <inheritdoc />
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(NotBoolean(flagKey, defaultValue));

    /// <inheritdoc />
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(NotBoolean(flagKey, defaultValue));

    private bool TryGetSwitch(string flagKey, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(flagKey) || !flagKey.StartsWith(KeyPrefix, StringComparison.Ordinal) || flagKey.Length == KeyPrefix.Length)
        {
            return false;
        }

        name = flagKey[KeyPrefix.Length..];
        return !string.IsNullOrWhiteSpace(name);
    }

    /// <summary>
    /// A key without the prefix is not ours: <c>FLAG_NOT_FOUND</c>, so a multi-provider moves on to the next provider.
    /// A prefixed key is a switch, which is boolean: <c>TYPE_MISMATCH</c>.
    /// </summary>
    private ResolutionDetails<T> NotBoolean<T>(string flagKey, T defaultValue)
        => TryGetSwitch(flagKey, out _)
            ? new(flagKey, defaultValue, ErrorType.TypeMismatch, Reason.Error, errorMessage: "Homeostat switches are boolean flags.")
            : new(flagKey, defaultValue, ErrorType.FlagNotFound, Reason.Error, errorMessage: $"'{flagKey}' is not a Homeostat switch.");

    private void OnSwitchChanged(object? sender, SwitchChangedEventArgs e)
    {
        // The flag key of a switch is the prefix followed by the switch name.
        EventChannel.Writer.TryWrite(new ProviderEventPayload
        {
            ProviderName = ProviderMetadata.Name,
            Type = ProviderEventTypes.ProviderConfigurationChanged,
            FlagsChanged = [KeyPrefix + e.Name],
            Message = $"Homeostat switch '{e.Name}' turned {(e.IsOn ? "on" : "off")}.",
        });
    }
}

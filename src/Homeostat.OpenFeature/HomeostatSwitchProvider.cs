using System.Collections.Concurrent;
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

    // Names of the switches that changed and are not announced yet. The SDK's event channel holds one event, so a
    // write while one is unread would fail and the change would be lost: changes are collected here (one entry per
    // switch, so it stays as small as the number of switches) and a single pump writes them, waiting for room.
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource _stopping = new();
    private int _subscribed;
    private int _pumping;

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
            if (_stopping.IsCancellationRequested)
            {
                _stopping = new CancellationTokenSource();
            }

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
            _stopping.Cancel();
            _pending.Clear();
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
        _pending[e.Name] = 0;
        StartPump();
    }

    private void StartPump()
    {
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) == 0)
        {
            _ = PumpAsync(Volatile.Read(ref _stopping).Token);
        }
    }

    private async Task PumpAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                List<string> changed = [];
                foreach (var name in _pending.Keys)
                {
                    if (_pending.TryRemove(name, out _))
                    {
                        changed.Add(name);
                    }
                }

                if (changed.Count > 0)
                {
                    // The flag key of a switch is the prefix followed by the switch name. Waits while the SDK has not
                    // read the previous event; changes made meanwhile go out together in the next one.
                    await EventChannel.Writer.WriteAsync(new ProviderEventPayload
                    {
                        ProviderName = ProviderMetadata.Name,
                        Type = ProviderEventTypes.ProviderConfigurationChanged,
                        FlagsChanged = [.. changed.Select(name => KeyPrefix + name)],
                        Message = $"Homeostat switches changed: {string.Join(", ", changed)}.",
                    }, stopping).ConfigureAwait(false);
                    continue;
                }

                // Stop, then look again: a change recorded after the last look but before the flag was cleared would
                // otherwise wait for the next change to be announced. A full fence (not a volatile write): the look
                // must not move before the flag is cleared, or a change recorded meanwhile would see the pump running
                // while the pump sees nothing to do.
                Interlocked.Exchange(ref _pumping, 0);
                if (_pending.IsEmpty || Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            // The provider was shut down, or the SDK closed its channel: nothing will read further events.
            Interlocked.Exchange(ref _pumping, 0);

            // A pump stopped by an earlier shutdown may end only after the provider was initialized again, and after
            // a change that found it still running: start a pump for the current subscription so that change is not
            // held until the next one. Only once per stale token: a pump on the current token stops for good.
            if (ex is OperationCanceledException && stopping != Volatile.Read(ref _stopping).Token
                && Volatile.Read(ref _subscribed) == 1 && !_pending.IsEmpty)
            {
                StartPump();
            }
        }
    }
}

using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Testing;

/// <summary>
/// Runs Homeostat in memory with simulated time, so tests can publish signals and assert on the
/// outcome of healing runs without waiting for real cooldowns or verification timeouts.
/// </summary>
/// <example>
/// <code>
/// await using var host = await HomeostatTestHost.StartAsync(h => h
///     .AddCondition("feed-down", c => c
///         .ForSource("feed")
///         .Heal(p => p.With("renew", sp => sp.GetRequiredService&lt;FeedClient&gt;().Renew()))),
///     services => services.AddSingleton&lt;FeedClient&gt;());
///
/// host.Publish("feed", SignalSeverity.Unhealthy);
/// var run = await host.WaitForRunAsync("feed-down");
/// </code>
/// </example>
public sealed class HomeostatTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;
    private readonly RunRecorder _recorder;
    private readonly HomeostatTestHostOptions _options;
    private readonly CancellationTokenSource _autoAdvance = new();
    private Task _autoAdvanceLoop = Task.CompletedTask;
    private int _disposed;

    private HomeostatTestHost(ServiceProvider provider, RunRecorder recorder, FakeTimeProvider time, HomeostatTestHostOptions options)
    {
        _provider = provider;
        _recorder = recorder;
        _options = options;
        Time = time;
        _hostedServices = provider.GetServices<IHostedService>().ToArray();
    }

    /// <summary>The simulated clock. Advance it manually when automatic advancing is disabled.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The host's services, to resolve your own components or Homeostat's.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Publishes signals; <see cref="Publish"/> is a shortcut.</summary>
    public ISignalPublisher Signals => _provider.GetRequiredService<ISignalPublisher>();

    /// <summary>Pause, resume and inspect Homeostat.</summary>
    public IHomeostatControl Control => _provider.GetRequiredService<IHomeostatControl>();

    /// <summary>The switches turned on by healing plans.</summary>
    public IHomeostatSwitches Switches => _provider.GetRequiredService<IHomeostatSwitches>();

    /// <summary>Approves or rejects steps that require approval.</summary>
    public IHomeostatApprovals Approvals => _provider.GetRequiredService<IHomeostatApprovals>();

    /// <summary>Every run completed so far, in completion order.</summary>
    public IReadOnlyList<HealingRunResult> CompletedRuns => _recorder.Runs;

    /// <summary>Creates and starts a host.</summary>
    /// <param name="configure">Homeostat configuration: conditions, plans, detectors, options.</param>
    /// <param name="configureServices">Registers your own services, such as the components your actions use.</param>
    /// <param name="options">
    /// Host settings. An object rather than a delegate on purpose: tests often share one instance (a start time, the
    /// clock steps) across several hosts.
    /// </param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<HomeostatTestHost> StartAsync(
        Action<HomeostatBuilder> configure,
        Action<IServiceCollection>? configureServices = null,
        HomeostatTestHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        options ??= new HomeostatTestHostOptions();

        var time = new FakeTimeProvider(options.StartTime);
        var recorder = new RunRecorder();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(time);
        configureServices?.Invoke(collection);
        collection.AddHomeostat(h =>
        {
            h.Configure(o => o.DryRun = options.DryRun);
            configure(h);
        });

        // Registered last so it is notified after the user's observers: when a test's wait completes,
        // every observer has already seen the run.
        collection.AddSingleton<IHealingObserver>(recorder);

        var host = new HomeostatTestHost(collection.BuildServiceProvider(validateScopes: true), recorder, time, options);
        try
        {
            // Same sequence as the generic host: options validated on start, then lifecycle callbacks and hosted services.
            host._provider.GetService<Microsoft.Extensions.Options.IStartupValidator>()?.Validate();

            foreach (var lifecycle in host._hostedServices.OfType<IHostedLifecycleService>())
            {
                await lifecycle.StartingAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var hosted in host._hostedServices)
            {
                await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var lifecycle in host._hostedServices.OfType<IHostedLifecycleService>())
            {
                await lifecycle.StartedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        host.StartAutoAdvance();
        return host;
    }

    /// <summary>Publishes a signal stamped with the simulated time.</summary>
    /// <param name="source">Signal source.</param>
    /// <param name="severity">Signal severity.</param>
    /// <param name="message">Optional message.</param>
    /// <param name="value">Optional measured value, for value, trend, anomaly, projection and seasonal rules.</param>
    public bool Publish(string source, SignalSeverity severity, string? message = null, double? value = null)
        => Signals.TryPublish(source, severity, message, value);

    /// <summary>
    /// Waits for a run of <paramref name="conditionName"/> to complete and returns its result.
    /// Each run is returned once: call again to get the next run of the same condition.
    /// </summary>
    /// <param name="conditionName">The condition.</param>
    /// <param name="timeout">How long to wait, in real time; <c>null</c> means <see cref="HomeostatTestHostOptions.DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Stops waiting with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="TimeoutException">No run completed in time.</exception>
    public async Task<HealingRunResult> WaitForRunAsync(string conditionName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? _options.DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);
        try
        {
            return await _recorder.NextAsync(conditionName, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No healing run of condition '{conditionName}' completed within {limit} (real time). " +
                $"Condition state: {Describe(conditionName)}.");
        }
    }

    /// <summary>Waits until a condition reaches <paramref name="status"/>.</summary>
    /// <param name="conditionName">The condition.</param>
    /// <param name="status">The status to wait for.</param>
    /// <param name="timeout">How long to wait, in real time; <c>null</c> means <see cref="HomeostatTestHostOptions.DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Stops waiting with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="TimeoutException">The condition did not reach the status in time.</exception>
    public async Task WaitForConditionAsync(string conditionName, ConditionStatus status, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? _options.DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);

        while (!cts.IsCancellationRequested)
        {
            if (GetCondition(conditionName)?.Status == status)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Once more: the status may have been reached while the last delay was being canceled.
        if (GetCondition(conditionName)?.Status == status)
        {
            return;
        }

        throw new TimeoutException(
            $"Condition '{conditionName}' did not reach {status} within {limit} (real time). Current state: {Describe(conditionName)}.");
    }

    /// <summary>
    /// Waits until a step of <paramref name="conditionName"/> is waiting for approval and returns the request.
    /// Simulated time keeps moving, so answer before the approval timeout expires.
    /// </summary>
    /// <param name="conditionName">The condition.</param>
    /// <param name="timeout">How long to wait, in real time; <c>null</c> means <see cref="HomeostatTestHostOptions.DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Stops waiting with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="TimeoutException">No approval was requested in time.</exception>
    public async Task<PendingApproval> WaitForApprovalAsync(string conditionName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? _options.DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);

        while (!cts.IsCancellationRequested)
        {
            if (await FindApprovalAsync(conditionName).ConfigureAwait(false) is { } pending)
            {
                return pending;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Once more: the request may have been made while the last delay was being canceled.
        if (await FindApprovalAsync(conditionName).ConfigureAwait(false) is { } late)
        {
            return late;
        }

        throw new TimeoutException(
            $"No approval request for condition '{conditionName}' within {limit} (real time). Condition state: {Describe(conditionName)}.");
    }

    private async Task<PendingApproval?> FindApprovalAsync(string conditionName)
        => (await Approvals.GetPendingAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(a => a.ConditionName == conditionName);

    /// <summary>Stops the services and releases the host. Calling it again does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        // Idempotent, like other disposables: a second call (an explicit dispose inside an await using) must not stop
        // disposed services again or cancel a disposed token source.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop the services first, while simulated time still moves, so anything waiting on it can finish;
        // bound the wait so a misbehaving action or verifier cannot hang the test. A service that throws does not keep
        // the others running: its error is reported once everything is stopped and disposed.
        var errors = new List<Exception>();
        using (var stopCts = new CancellationTokenSource(_options.DefaultTimeout))
        {
            // Enumerable.Reverse explicitly: on C# 14 array.Reverse() binds to the in-place span overload.
            var reversed = Enumerable.Reverse(_hostedServices).ToArray();
            foreach (var lifecycle in reversed.OfType<IHostedLifecycleService>())
            {
                await StopSafelyAsync(() => lifecycle.StoppingAsync(stopCts.Token), errors).ConfigureAwait(false);
            }

            foreach (var hosted in reversed)
            {
                await StopSafelyAsync(() => hosted.StopAsync(stopCts.Token), errors).ConfigureAwait(false);
            }

            foreach (var lifecycle in reversed.OfType<IHostedLifecycleService>())
            {
                await StopSafelyAsync(() => lifecycle.StoppedAsync(stopCts.Token), errors).ConfigureAwait(false);
            }
        }

        _autoAdvance.Cancel();
        try
        {
            await _autoAdvanceLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await _provider.DisposeAsync().ConfigureAwait(false);
        _autoAdvance.Dispose();

        if (errors.Count > 0)
        {
            throw new AggregateException("Some services failed to stop.", errors);
        }
    }

    private static async Task StopSafelyAsync(Func<Task> stop, List<Exception> errors)
    {
        try
        {
            await stop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout reached; keep stopping the others.
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private ConditionSnapshot? GetCondition(string name)
        => Control.GetConditions().FirstOrDefault(c => c.Name == name);

    private string Describe(string name)
        => GetCondition(name) is { } c ? $"{c.Status}, last signal at {c.LastSignalAt?.ToString("O") ?? "never"}" : "unknown condition";

    private void StartAutoAdvance()
    {
        if (_options.AutoAdvanceStep is not { } step)
        {
            return;
        }

        var token = _autoAdvance.Token;
        _autoAdvanceLoop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.AutoAdvanceInterval, token).ConfigureAwait(false);
                Time.Advance(step);
            }
        }, token);
    }
}

using System.Collections.Concurrent;
using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Engine;

/// <summary>Executes healing plans under the configured guardrails.</summary>
/// <remarks>
/// The semaphore is intentionally not disposed: runs may outlive the host shutdown timeout, and a
/// <see cref="SemaphoreSlim"/> whose wait handle is never used holds no unmanaged resources.
/// </remarks>
internal sealed class HealingRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HomeostatOptions _options;
    private readonly ActionBudget _budget;
    private readonly IHomeostatControl _control;
    private readonly ObserverNotifier _notifier;
    private readonly ConditionSet _conditions;
    private readonly HomeostatApprovals _approvals;
    private readonly RunHistory _history;
    private readonly SwitchOwnership _ownership;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HealingRunner> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunEnded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    public HealingRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<HomeostatOptions> options,
        ActionBudget budget,
        IHomeostatControl control,
        ObserverNotifier notifier,
        ConditionSet conditions,
        HomeostatApprovals approvals,
        RunHistory history,
        SwitchOwnership ownership,
        TimeProvider timeProvider,
        ILogger<HealingRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _budget = budget;
        _control = control;
        _notifier = notifier;
        _conditions = conditions;
        _approvals = approvals;
        _history = history;
        _ownership = ownership;
        _timeProvider = timeProvider;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentHealings, _options.MaxConcurrentHealings);
    }

    /// <summary>
    /// Starts a healing run in the background unless guardrails prevent it.
    /// Returns <c>true</c> if a run was started.
    /// </summary>
    public bool TryStart(ConditionTracker tracker, Signal trigger, CancellationToken stoppingToken)
    {
        var name = tracker.Definition.Name;
        var plan = tracker.Definition.Plan;

        if (plan is null)
        {
            _logger.LogDebug("Condition {Condition} has no healing plan.", name);
            return false;
        }

        if (_control.IsPaused)
        {
            // Debug: an active condition reports this on every triggering signal while paused.
            _logger.LogDebug("Condition {Condition} is active, but Homeostat is paused.", name);
            return false;
        }

        if (_conditions.FindActiveSuppressor(tracker.Definition) is { } cause)
        {
            _logger.LogDebug("Condition {Condition} is active, but suppressed by {Cause}.", name, cause);
            return false;
        }

        if (_held.ContainsKey(name))
        {
            _logger.LogDebug("Condition {Condition} is active, but its plan is held after a rejected approval.", name);
            return false;
        }

        // Claim the slot before checking the cooldown: a run that ends in between writes its end time
        // before releasing the slot, so the check below always sees it.
        if (!_running.TryAdd(name, 0))
        {
            _logger.LogDebug("A healing run for condition {Condition} is already in progress.", name);
            return false;
        }

        var cooldown = plan.Cooldown ?? _options.DefaultCooldown;
        if (_lastRunEnded.TryGetValue(name, out var ended) && _timeProvider.GetUtcNow() - ended < cooldown)
        {
            _running.TryRemove(name, out _);
            _logger.LogDebug("Condition {Condition} is in cooldown until {Until}.", name, ended + cooldown);
            return false;
        }

        var task = Task.Run(() => RunAsync(tracker, plan, trigger, stoppingToken), CancellationToken.None);
        _inFlight.TryAdd(task, 0);
        _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    /// <summary>Called when the condition clears: a plan held after a rejection may run again next time.</summary>
    public void ReleaseHold(string conditionName) => _held.TryRemove(conditionName, out _);

    /// <summary>Completes when all in-flight runs have finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task RunAsync(ConditionTracker tracker, HealingPlan plan, Signal trigger, CancellationToken cancellationToken)
    {
        var name = plan.ConditionName;
        var startedAt = _timeProvider.GetUtcNow();
        var startTimestamp = _timeProvider.GetTimestamp();
        var steps = new List<HealingStepResult>();
        var outcome = HealingRunOutcome.Failed;

        using var activity = HomeostatDiagnostics.ActivitySource.StartActivity("homeostat.heal");
        activity?.SetTag("homeostat.condition", name);
        activity?.SetTag("homeostat.signal.source", trigger.Source);
        HomeostatDiagnostics.Runs.Add(1, new KeyValuePair<string, object?>("condition", name));

        var slot = new ConcurrencySlot(_concurrency);
        try
        {
            await slot.AcquireAsync(cancellationToken).ConfigureAwait(false);
            outcome = await ExecutePlanAsync(tracker, plan, trigger, steps, slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Anything raised while the host is stopping is a consequence of the shutdown.
            outcome = HealingRunOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Healing run for condition {Condition} failed unexpectedly.", name);
            outcome = HealingRunOutcome.Failed;
        }
        finally
        {
            var outcomeName = outcome.ToString();
            var tags = new[]
            {
                new KeyValuePair<string, object?>("condition", name),
                new KeyValuePair<string, object?>("outcome", outcomeName),
            };
            HomeostatDiagnostics.Outcomes.Add(1, tags);
            HomeostatDiagnostics.Duration.Record(_timeProvider.GetElapsedTime(startTimestamp).TotalSeconds, tags);
            activity?.SetTag("homeostat.outcome", outcomeName);

            slot.Release();

            var completedAt = _timeProvider.GetUtcNow();
            _lastRunEnded[name] = completedAt;
            _running.TryRemove(name, out _);

            var result = new HealingRunResult(name, outcome, steps.ToArray(), startedAt, completedAt);
            _history.Add(result);
            _notifier.Notify(o => o.OnRunCompleted(result));
        }
    }

    private async Task<HealingRunOutcome> ExecutePlanAsync(
        ConditionTracker tracker,
        HealingPlan plan,
        Signal trigger,
        List<HealingStepResult> results,
        ConcurrencySlot slot,
        CancellationToken cancellationToken)
    {
        var name = plan.ConditionName;

        if (_options.DryRun)
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].RequiresApproval)
                {
                    _logger.LogWarning(
                        "[dry-run] Condition {Condition}: step {Step}/{Total} would request approval, then execute {Action}.",
                        name, i + 1, plan.Steps.Count, plan.Steps[i].Name);
                }
                else
                {
                    _logger.LogWarning(
                        "[dry-run] Condition {Condition}: step {Step}/{Total} would execute {Action}.",
                        name, i + 1, plan.Steps.Count, plan.Steps[i].Name);
                }

                RecordAction(name, plan.Steps[i].Name, dryRun: true);
            }

            return HealingRunOutcome.DryRun;
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];

            if (CheckStop(tracker, i) is { } stop)
            {
                return stop;
            }

            if (step.RequiresApproval)
            {
                // Waiting for a person must not block the other plans: give the concurrency slot back meanwhile.
                slot.Release();
                var approval = await WaitForApprovalAsync(name, i, plan.Steps.Count, step, trigger, cancellationToken).ConfigureAwait(false);
                await slot.AcquireAsync(cancellationToken).ConfigureAwait(false);

                // The world may have changed while waiting (cleared, paused, suppressed).
                if (CheckStop(tracker, i) is { } stopAfterApproval)
                {
                    return stopAfterApproval;
                }

                if (approval != ApprovalStatus.Approved)
                {
                    if (approval == ApprovalStatus.Rejected)
                    {
                        _held.TryAdd(name, 0);
                        _logger.LogWarning(
                            "Condition {Condition}: plan held after the rejection; it will run again only after the condition clears.", name);
                    }

                    return HealingRunOutcome.NotApproved;
                }
            }

            if (!_budget.TryConsume(_timeProvider.GetUtcNow()))
            {
                _logger.LogError(
                    "Condition {Condition}: action budget exhausted ({Max} actions per {Window}). Manual intervention required.",
                    name, _options.MaxActionsPerWindow, _options.ActionBudgetWindow);
                return HealingRunOutcome.BudgetExhausted;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = new HealingContext(
                name, trigger, i, step.Name, _timeProvider.GetUtcNow(), scope.ServiceProvider, tracker.Snapshot)
            {
                ClearAfter = tracker.Definition.ClearAfter,
            };

            _logger.LogWarning(
                "Condition {Condition}: executing step {Step}/{Total} {Action}.",
                name, i + 1, plan.Steps.Count, step.Name);
            RecordAction(name, step.Name, dryRun: false);

            var (executed, error) = await TryExecuteAsync(context, step, plan, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            context.ActionCompletedAt = _timeProvider.GetUtcNow();

            if (!executed)
            {
                Report(results, new HealingStepResult(name, i, step.Name, Succeeded: false, Verification: null, error));
                continue;
            }

            if (step.SwitchToResetOnClear is { } switchName)
            {
                // Only switches Homeostat actually turned on are turned off when the condition clears.
                _ownership.Claim(switchName, name);
            }

            var verifierType = plan.VerifierType ?? typeof(ConditionClearedVerifier);
            var verifier = (IHealingVerifier)scope.ServiceProvider.GetRequiredService(verifierType);
            var timeout = plan.VerificationTimeout ?? _options.DefaultVerificationTimeout;
            VerificationOutcome verification;
            Exception? verificationError = null;
            try
            {
                verification = await verifier.VerifyAsync(context, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A broken verifier must not hide the step: the result cannot be judged, so stop escalating.
                _logger.LogError(ex, "Condition {Condition}: verification after {Action} threw an exception.", name, step.Name);
                verification = VerificationOutcome.Inconclusive;
                verificationError = ex;
            }

            Report(results, new HealingStepResult(name, i, step.Name, Succeeded: true, verification, verificationError));

            switch (verification)
            {
                case VerificationOutcome.Healed:
                    _logger.LogInformation("Condition {Condition}: healed by {Action}.", name, step.Name);
                    return HealingRunOutcome.Healed;

                case VerificationOutcome.Inconclusive:
                    _logger.LogWarning(
                        "Condition {Condition}: verification after {Action} was inconclusive; not escalating further.",
                        name, step.Name);
                    return HealingRunOutcome.Inconclusive;

                default:
                    _logger.LogWarning("Condition {Condition}: {Action} did not heal the condition.", name, step.Name);
                    break;
            }
        }

        _logger.LogError(
            "Condition {Condition}: all {Count} healing steps were tried without success. Manual intervention required.",
            name, plan.Steps.Count);
        return HealingRunOutcome.NotHealed;
    }

    private HealingRunOutcome? CheckStop(ConditionTracker tracker, int index)
    {
        var name = tracker.Definition.Name;

        if (_control.IsPaused)
        {
            _logger.LogWarning("Condition {Condition}: Homeostat was paused, stopping before step {Step}.", name, index + 1);
            return HealingRunOutcome.Paused;
        }

        if (_conditions.FindActiveSuppressor(tracker.Definition) is { } cause)
        {
            _logger.LogWarning("Condition {Condition}: suppressed by {Cause}, stopping before step {Step}.", name, cause, index + 1);
            return HealingRunOutcome.Suppressed;
        }

        if (tracker.Snapshot().Status != ConditionStatus.Active)
        {
            _logger.LogInformation("Condition {Condition}: cleared before step {Step}; nothing more to do.", name, index + 1);
            return HealingRunOutcome.ConditionCleared;
        }

        return null;
    }

    private async Task<ApprovalStatus> WaitForApprovalAsync(
        string name, int index, int total, HealingStep step, Signal trigger, CancellationToken cancellationToken)
    {
        var timeout = step.ApprovalTimeout ?? _options.DefaultApprovalTimeout;
        var requestedAt = _timeProvider.GetUtcNow();
        var request = new PendingApproval(Guid.NewGuid().ToString("N"), name, index, step.Name, requestedAt, requestedAt + timeout, trigger);
        var decision = _approvals.Register(request);

        _logger.LogWarning(
            "Condition {Condition}: step {Step}/{Total} {Action} is waiting for approval {ApprovalId} until {ExpiresAt}.",
            name, index + 1, total, step.Name, request.Id, request.ExpiresAt);
        _notifier.Notify(o => o.OnApprovalRequested(request));

        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var expiry = Task.Delay(timeout, _timeProvider, delayCts.Token);
            await Task.WhenAny(decision, expiry).ConfigureAwait(false);

            // Each call is a no-op if someone answered first; the decision task then carries the answer.
            if (cancellationToken.IsCancellationRequested)
            {
                _approvals.Cancel(request.Id);
            }
            else
            {
                _approvals.Expire(request.Id);
            }

            delayCts.Cancel();
        }

        var (status, by) = await decision.ConfigureAwait(false);

        // Notify before anything else, so every request in the audit trail is closed, even on shutdown.
        var resolution = new ApprovalResolution(request, status, by, _timeProvider.GetUtcNow());
        _notifier.Notify(o => o.OnApprovalResolved(resolution));

        switch (status)
        {
            case ApprovalStatus.Approved:
                _logger.LogWarning("Condition {Condition}: approval {ApprovalId} for {Action} granted by {By}.", name, request.Id, step.Name, by ?? "unknown");
                break;

            case ApprovalStatus.Rejected:
                _logger.LogWarning("Condition {Condition}: approval {ApprovalId} for {Action} rejected by {By}.", name, request.Id, step.Name, by ?? "unknown");
                break;

            case ApprovalStatus.Expired:
                _logger.LogError("Condition {Condition}: approval {ApprovalId} for {Action} expired. Manual intervention required.", name, request.Id, step.Name);
                break;

            default:
                _logger.LogInformation("Condition {Condition}: approval {ApprovalId} for {Action} withdrawn.", name, request.Id, step.Name);
                cancellationToken.ThrowIfCancellationRequested();
                break;
        }

        return status;
    }

    /// <summary>The run's claim on <see cref="HomeostatOptions.MaxConcurrentHealings"/>, released at most once.</summary>
    private sealed class ConcurrencySlot(SemaphoreSlim semaphore)
    {
        private bool _held;

        public async Task AcquireAsync(CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            _held = true;
        }

        public void Release()
        {
            if (_held)
            {
                _held = false;
                semaphore.Release();
            }
        }
    }

    private async Task<(bool Executed, Exception? Error)> TryExecuteAsync(
        HealingContext context,
        HealingStep step,
        HealingPlan plan,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var timeout = plan.ActionTimeout ?? _options.DefaultActionTimeout;

        // The timeout follows the TimeProvider, so it can be tested with simulated time.
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var action = step.Resolve(services);
            await action.ExecuteAsync(context, linkedCts.Token).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Condition {Condition}: {Action} timed out.", context.ConditionName, step.Name);
            return (false, new TimeoutException($"Step '{step.Name}' did not complete within {timeout}.", ex));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes cancellations raised by the action itself (for example its own HttpClient timeout).
            _logger.LogError(ex, "Condition {Condition}: {Action} threw an exception.", context.ConditionName, step.Name);
            return (false, ex);
        }
    }

    private void Report(List<HealingStepResult> results, HealingStepResult result)
    {
        results.Add(result);
        _notifier.Notify(o => o.OnStepCompleted(result));
    }

    private static void RecordAction(string condition, string step, bool dryRun)
        => HomeostatDiagnostics.Actions.Add(
            1,
            new KeyValuePair<string, object?>("condition", condition),
            new KeyValuePair<string, object?>("action", step),
            new KeyValuePair<string, object?>("dry_run", dryRun));
}

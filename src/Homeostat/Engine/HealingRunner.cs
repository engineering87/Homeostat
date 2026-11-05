using System.Collections.Concurrent;
using Homeostat.Conditions;
using Homeostat.Coordination;
using Homeostat.Diagnostics;
using Homeostat.Healing;
using Homeostat.Persistence;
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
    private readonly IHomeostatStore _store;
    private readonly IHomeostatCoordinator _coordinator;
    private readonly IHomeostatControl _control;
    private readonly ObserverNotifier _notifier;
    private readonly ConditionSet _conditions;
    private readonly HomeostatApprovals _approvals;
    private readonly SwitchOwnership _ownership;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HealingRunner> _logger;
    private readonly SemaphoreSlim _concurrency;
    // One entry per running plan; the value identifies the run, so a run only ever removes its own entry.
    private readonly ConcurrentDictionary<string, object> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    // Last known plan state (see PlanStateCache). Another instance (or an operator) may lift a hold, so a cached hold
    // is trusted only for a while.
    private readonly ConcurrentDictionary<string, PlanState> _knownState;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _heldReadAt;
    private static readonly TimeSpan HeldRecheckInterval = TimeSpan.FromMinutes(1);

    private readonly HomeostatSwitches _switches;

    // Plans whose condition cleared, while the reset is being written to the store.
    private readonly ConcurrentDictionary<string, byte> _pendingResets = new(StringComparer.Ordinal);

    // Last time a store failure was logged as an error, per condition, to avoid one error per triggering signal.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _storeErrors = new(StringComparer.Ordinal);

    public HealingRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<HomeostatOptions> options,
        IHomeostatStore store,
        IHomeostatCoordinator coordinator,
        IHomeostatControl control,
        ObserverNotifier notifier,
        ConditionSet conditions,
        HomeostatApprovals approvals,
        SwitchOwnership ownership,
        TimeProvider timeProvider,
        ILogger<HealingRunner> logger,
        HomeostatSwitches switches,
        PlanStateCache planStates)
    {
        _switches = switches;
        _knownState = planStates.Known;
        _heldReadAt = planStates.HeldReadAt;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _store = store;
        _coordinator = coordinator;
        _control = control;
        _notifier = notifier;
        _conditions = conditions;
        _approvals = approvals;
        _ownership = ownership;
        _timeProvider = timeProvider;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentHealings, _options.MaxConcurrentHealings);
    }

    /// <summary>
    /// Starts a healing run in the background unless guardrails prevent it.
    /// Returns <c>true</c> if a run may start (the cooldown and the cluster lease are checked in the background).
    /// </summary>
    public bool TryStart(ConditionTracker tracker, Signal trigger, CancellationToken stoppingToken)
    {
        // Runs for every triggering signal of an active condition, also during a cooldown or a pause: the debug logs
        // below are guarded, so nothing is allocated for them when debug logging is off.
        _hostStopping = stoppingToken;
        var definition = tracker.Definition;
        var name = definition.Name;
        var plan = definition.Plan;

        if (plan is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Condition {Condition} has no healing plan.", name);
            }
            return false;
        }

        if (_control.IsPaused)
        {
            // Debug: an active condition reports this on every triggering signal while paused.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Condition {Condition} is active, but Homeostat is paused.", name);
            }
            return false;
        }

        if (_conditions.FindActiveSuppressor(definition) is { } cause)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Condition {Condition} is active, but suppressed by {Cause}.", name, cause);
            }
            return false;
        }

        var key = StateKey(plan);
        var now = _timeProvider.GetUtcNow();
        if (_knownState.TryGetValue(key, out var known)
            && !(known.Held && (!_heldReadAt.TryGetValue(key, out var readAt) || now - readAt >= HeldRecheckInterval))
            && Blocked(plan, known, now, describe: _logger.IsEnabled(LogLevel.Debug)) is { } reason)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Condition {Condition}: not starting, {Reason}.", name, reason);
            }
            return false;
        }

        var run = new object();
        if (!_running.TryAdd(name, run))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("A healing run for condition {Condition} is already in progress.", name);
            }
            return false;
        }

        var task = Task.Run(() => StartAsync(tracker, plan, trigger, run, stoppingToken), CancellationToken.None);
        _inFlight.TryAdd(task, 0);
        _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    /// <summary>
    /// Called when the condition clears: a plan held after a rejection may run again, and the cooldown backoff restarts.
    /// </summary>
    public void OnConditionCleared(ConditionDefinition definition)
    {
        if (definition.Plan is not { } plan)
        {
            return;
        }

        // For a cluster plan, only the instance that ran the plan last resets it: one recovered instance must not lift
        // a rejection or a backoff that concerns the whole cluster.
        var key = StateKey(plan);
        var update = new PlanStateUpdate
        {
            Failures = FailureCountChange.Reset,
            Held = false,
            OnlyIfLastRunBy = plan.Scope == PlanScope.Cluster ? _options.InstanceId : null,
        };

        // Apply the reset locally at once, so a triggering signal that follows the clear is not blocked by stale state.
        _pendingResets[key] = 0;
        _knownState.AddOrUpdate(key, PlanState.Empty, (_, state) => update.ApplyTo(state));

        var task = Task.Run(async () =>
        {
            try
            {
                _knownState[key] = await SharedControl.Bounded(() => _store.UpdatePlanStateAsync(key, update, CancellationToken.None), SharedControl.WriteTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Condition {Condition}: could not reset the plan state in the store.", definition.Name);
            }
            finally
            {
                _pendingResets.TryRemove(key, out _);
            }
        });
        _inFlight.TryAdd(task, 0);
        _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Completes when all in-flight runs have finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Keys);

    /// <summary>Key of a plan in the store: shared for cluster plans, per instance otherwise.</summary>
    internal string StateKey(HealingPlan plan)
        => plan.Scope == PlanScope.Cluster
            ? plan.ConditionName
            : _stateKeys.GetValue(plan, p => $"{_options.InstanceId}/{p.ConditionName}");   // built once per plan: asked for on every triggering signal

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HealingPlan, string> _stateKeys = new();

    /// <summary>Effective cooldown, with backoff.</summary>
    internal TimeSpan EffectiveCooldown(HealingPlan plan, PlanState state)
    {
        var cooldown = plan.Cooldown ?? _options.DefaultCooldown;
        if (plan.CooldownBackoff <= 1 || state.ConsecutiveFailures <= 0)
        {
            return cooldown;
        }

        // The backoff only ever lengthens the cooldown, even with a maximum set below it.
        var max = plan.MaxCooldown is { } configured && configured > cooldown ? configured : cooldown;
        var ticks = cooldown.Ticks * Math.Pow(plan.CooldownBackoff, Math.Min(state.ConsecutiveFailures, 64));
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    private void LogStoreFailure(string condition, Exception exception, string message)
    {
        var now = _timeProvider.GetUtcNow();
        if (_storeErrors.TryGetValue(condition, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            _logger.LogDebug(exception, message, condition);
            return;
        }

        _storeErrors[condition] = now;
        _logger.LogError(exception, message, condition);
    }

    /// <summary>Why the plan cannot start now, or <c>null</c>; without <paramref name="describe"/>, a reason that is not formatted.</summary>
    private string? Blocked(HealingPlan plan, PlanState state, DateTimeOffset now, bool describe = true)
    {
        if (state.Held)
        {
            return "its plan is held after a rejected approval";
        }

        if (state.LastRunEndedAt is { } ended)
        {
            var cooldown = EffectiveCooldown(plan, state);
            if (now - ended < cooldown)
            {
                return describe ? $"cooldown until {ended + cooldown:O}" : "cooldown";
            }
        }

        return null;
    }

    private async Task StartAsync(ConditionTracker tracker, HealingPlan plan, Signal trigger, object run, CancellationToken cancellationToken)
    {
        var name = plan.ConditionName;
        IHomeostatLease? lease = null;
        CancellationTokenSource? runCts = null;
        try
        {
            if (plan.Scope == PlanScope.Cluster)
            {
                try
                {
                    // Bounded; a lease that arrives after Homeostat stopped waiting is released at once, or its renewal
                    // would keep every other instance from running the plan.
                    var acquiring = Task.Run(
                        () => _coordinator.TryAcquireAsync($"plan:{name}", _options.ClusterLeaseDuration, cancellationToken).AsTask(),
                        CancellationToken.None);
                    try
                    {
                        lease = await acquiring.WaitAsync(SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        _ = acquiring.ContinueWith(
                            static t => t.IsCompletedSuccessfully && t.Result is { } late ? late.DisposeAsync().AsTask() : Task.CompletedTask,
                            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
                            .ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        throw;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Fail closed, like the store: without the lease another instance could run the same plan.
                    LogStoreFailure(name, ex, "Condition {Condition}: the coordinator is unavailable; not starting the cluster plan.");
                    return;
                }

                if (lease is null)
                {
                    _logger.LogDebug("Condition {Condition}: the plan is running on another instance.", name);
                    return;
                }
            }

            var key = StateKey(plan);
            PlanState state;
            try
            {
                state = await SharedControl.Bounded(() => _store.GetPlanStateAsync(key, cancellationToken), SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Fail closed: without the cooldown, a run could repeat too soon.
                LogStoreFailure(name, ex, "Condition {Condition}: the store is unavailable; not starting the plan.");
                return;
            }

            if (_pendingResets.ContainsKey(key) && _knownState.TryGetValue(key, out var pending)
                && (plan.Scope != PlanScope.Cluster || string.Equals(state.LastRunBy, _options.InstanceId, StringComparison.Ordinal)))
            {
                // The reset of a clear is still being written: trust the local view for the fields it changes. For a
                // cluster plan only when this instance ran it last, as the reset itself requires.
                state = state with { Held = pending.Held, ConsecutiveFailures = pending.ConsecutiveFailures };
            }

            _knownState[key] = state;
            _heldReadAt[key] = _timeProvider.GetUtcNow();
            if (Blocked(plan, state, _timeProvider.GetUtcNow()) is { } reason)
            {
                _logger.LogDebug("Condition {Condition}: not starting, {Reason}.", name, reason);
                return;
            }

            // A cluster run stops before its next step if the lease may have been lost.
            runCts = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
            var saved = await RunAsync(tracker, plan, trigger, key, run, runCts?.Token ?? cancellationToken).ConfigureAwait(false);
            if (!saved && lease is not null && !lease.Lost.IsCancellationRequested)
            {
                // The cooldown exists only in this instance's memory: releasing the lease now would let another
                // instance run the plan again at once. Keep holding it for the cooldown instead.
                KeepLeaseForCooldown(lease, plan, name, cancellationToken);
                lease = null;
            }

            if (lease is not null && lease.Lost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Condition {Condition}: the cluster lease was lost during the run, which was stopped.", name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Condition {Condition}: could not start the plan.", name);
        }
        finally
        {
            runCts?.Dispose();
            if (lease is not null)
            {
                try
                {
                    var held = lease;
                    await SharedControl.Bounded(() => held.DisposeAsync(), SharedControl.WriteTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Condition {Condition}: could not release the cluster lease; it will expire.", name);
                }
            }

            _running.TryRemove(new KeyValuePair<string, object>(name, run));
        }
    }

    private void KeepLeaseForCooldown(IHomeostatLease lease, HealingPlan plan, string name, CancellationToken stoppingToken)
    {
        var cooldown = EffectiveCooldown(plan, _knownState.TryGetValue(StateKey(plan), out var known) ? known : PlanState.Empty);
        _logger.LogWarning("Condition {Condition}: the plan state was not saved; keeping the cluster lease for the cooldown ({Cooldown}).", name, cooldown);
        _ = Task.Run(async () =>
        {
            try
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Lost);
                await Task.Delay(cooldown, _timeProvider, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping, or the lease was lost anyway.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Condition {Condition}: could not wait for the cooldown; releasing the cluster lease now.", name);
            }

            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Condition {Condition}: could not release the cluster lease; it will expire.", name);
            }
        }, CancellationToken.None);
    }

    /// <summary>Runs the plan and records the outcome; <c>false</c> when the plan state could not be saved.</summary>
    private async Task<bool> RunAsync(ConditionTracker tracker, HealingPlan plan, Signal trigger, string key, object run, CancellationToken cancellationToken)
    {
        var name = plan.ConditionName;
        var startedAt = _timeProvider.GetUtcNow();
        var startTimestamp = _timeProvider.GetTimestamp();
        var steps = new List<HealingStepResult>();
        var runId = Guid.NewGuid().ToString("N");
        var outcome = HealingRunOutcome.Failed;
        var held = false;

        using var activity = TelemetryEnabled ? HomeostatDiagnostics.ActivitySource.StartActivity("homeostat.heal") : null;
        activity?.SetTag("homeostat.condition", name);
        activity?.SetTag("homeostat.signal.source", trigger.Source);
        activity?.SetTag("homeostat.instance", _options.InstanceId);
        activity?.SetTag("homeostat.run.id", runId);
        if (TelemetryEnabled)
        {
            HomeostatDiagnostics.Runs.Add(1, new KeyValuePair<string, object?>("condition", name));
        }

        var slot = new ConcurrencySlot(_concurrency);
        try
        {
            await slot.AcquireAsync(cancellationToken).ConfigureAwait(false);
            (outcome, held) = await ExecutePlanAsync(tracker, plan, trigger, runId, steps, slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Anything raised while the host is stopping is a consequence of the shutdown.
            outcome = HealingRunOutcome.Canceled;
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
            if (TelemetryEnabled)
            {
                HomeostatDiagnostics.Outcomes.Add(1, tags);
                HomeostatDiagnostics.Duration.Record(_timeProvider.GetElapsedTime(startTimestamp).TotalSeconds, tags);
            }
            activity?.SetTag("homeostat.outcome", outcomeName);

            slot.Release();
        }

        var completedAt = _timeProvider.GetUtcNow();
        var result = new HealingRunResult
        {
            RunId = runId,
            ConditionName = name,
            Instance = _options.InstanceId,
            Outcome = outcome,
            Steps = steps.ToArray(),
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
        var saved = true;

        // Persist before notifying, so a test or an operator who sees the run also sees its cooldown. Use a fresh
        // token: the outcome of a run canceled by the shutdown is still worth recording. Real time, enough for the three
        // writes below (each bounded by the write timeout).
        using (var persistCts = new CancellationTokenSource(3 * SharedControl.WriteTimeout))
        {
            var failed = outcome is HealingRunOutcome.NotHealed or HealingRunOutcome.Inconclusive or HealingRunOutcome.Failed;
            var update = new PlanStateUpdate
            {
                LastRunEndedAt = completedAt,
                LastRunBy = _options.InstanceId,
                Failures = outcome == HealingRunOutcome.Healed ? FailureCountChange.Reset
                    : failed ? FailureCountChange.Increment
                    : FailureCountChange.Keep,
                Held = held ? true : null,
            };

            try
            {
                _knownState[key] = await SharedControl.Bounded(() => _store.UpdatePlanStateAsync(key, update, persistCts.Token), SharedControl.WriteTimeout, persistCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Keep the cooldown locally at least, so the plan does not repeat at once.
                _knownState[key] = update.ApplyTo(_knownState.TryGetValue(key, out var known) ? known : PlanState.Empty);
                _logger.LogError(ex, "Condition {Condition}: could not save the plan state.", name);
                saved = false;
            }

            // The condition cleared while the outcome was being saved: the reset that the clear wrote may have landed
            // first, and this hold or failure count would then outlive the episode it belongs to.
            if (saved && (update.Held == true || update.Failures == FailureCountChange.Increment)
                && tracker.Snapshot().Status != ConditionStatus.Active)
            {
                try
                {
                    var reset = new PlanStateUpdate { Held = false, Failures = FailureCountChange.Reset, OnlyIfLastRunBy = _options.InstanceId };
                    _knownState[key] = await SharedControl.Bounded(() => _store.UpdatePlanStateAsync(key, reset, persistCts.Token), SharedControl.WriteTimeout, persistCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Condition {Condition}: cleared during the run, but its plan state could not be reset.", name);
                }
            }

            try
            {
                await SharedControl.Bounded(() => _store.AppendRunAsync(HealingRunRecord.From(result, _options.InstanceId), _options.RunHistorySize, persistCts.Token), SharedControl.WriteTimeout, persistCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Condition {Condition}: could not record the run in the history.", name);
            }
        }

        // Free the slot before observers hear about the run, so a new triggering signal can start the next one.
        _running.TryRemove(new KeyValuePair<string, object>(name, run));
        _notifier.Notify(o => o.OnRunCompleted(result));
        return saved;
    }

    /// <summary>The steps of this run: the declared order, or the learned one for an adaptive plan.</summary>
    private async Task<IReadOnlyList<HealingStep>> OrderStepsAsync(HealingPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AdaptiveMinimumAttempts is not { } minimum || _options.RunHistorySize <= 0)
        {
            return plan.Steps;
        }

        IReadOnlyList<HealingRunRecord> history;
        try
        {
            history = await SharedControl.Bounded(() => _store.GetRecentRunsAsync(_options.RunHistorySize, cancellationToken), SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Condition {Condition}: run history unavailable; declared step order.", plan.ConditionName);
            return plan.Steps;
        }

        var ordered = AdaptiveOrder.Order(plan.Steps, history, plan.ConditionName, minimum);
        if (!ordered.SequenceEqual(plan.Steps))
        {
            _logger.LogInformation("Condition {Condition}: steps ordered by measured success: {Steps}.",
                plan.ConditionName, string.Join(", ", ordered.Select(s => s.Name)));
        }

        return ordered;
    }

    private async Task<(HealingRunOutcome Outcome, bool Held)> ExecutePlanAsync(
        ConditionTracker tracker,
        HealingPlan plan,
        Signal trigger,
        string runId,
        List<HealingStepResult> results,
        ConcurrencySlot slot,
        CancellationToken cancellationToken)
    {
        var name = plan.ConditionName;
        var planSteps = await OrderStepsAsync(plan, cancellationToken).ConfigureAwait(false);

        if (_options.DryRun)
        {
            for (var i = 0; i < planSteps.Count; i++)
            {
                if (planSteps[i].RequiresApproval)
                {
                    _logger.LogWarning(
                        "[dry-run] Condition {Condition}: step {Step}/{Total} would request approval, then execute {Action}.",
                        name, i + 1, planSteps.Count, planSteps[i].Name);
                }
                else
                {
                    _logger.LogWarning(
                        "[dry-run] Condition {Condition}: step {Step}/{Total} would execute {Action}.",
                        name, i + 1, planSteps.Count, planSteps[i].Name);
                }

                RecordAction(name, planSteps[i].Name, dryRun: true);
            }

            return (HealingRunOutcome.DryRun, false);
        }

        for (var i = 0; i < planSteps.Count; i++)
        {
            var step = planSteps[i];

            if (CheckStop(tracker, i) is { } stop)
            {
                return (stop, false);
            }

            if (step.RequiresApproval)
            {
                // Waiting for a person must not block the other plans: give the concurrency slot back meanwhile.
                slot.Release();
                var approval = await WaitForApprovalAsync(name, i, planSteps.Count, step, trigger, cancellationToken).ConfigureAwait(false);
                await slot.AcquireAsync(cancellationToken).ConfigureAwait(false);

                // The world may have changed while waiting (cleared, paused, suppressed).
                if (CheckStop(tracker, i) is { } stopAfterApproval)
                {
                    return (stopAfterApproval, false);
                }

                if (approval != ApprovalStatus.Approved)
                {
                    var rejected = approval == ApprovalStatus.Rejected;
                    if (rejected)
                    {
                        _logger.LogWarning(
                            "Condition {Condition}: plan held after the rejection; it will run again only after the condition clears.", name);
                    }

                    return (HealingRunOutcome.NotApproved, rejected);
                }
            }

            bool allowed;
            try
            {
                var now = _timeProvider.GetUtcNow();
                allowed = await SharedControl.Bounded(
                    () => _store.TryConsumeBudgetAsync(now, _options.MaxActionsPerWindow, _options.ActionBudgetWindow, cancellationToken),
                    SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Fail closed: an action whose budget cannot be checked does not run.
                LogStoreFailure(name, ex, "Condition {Condition}: the store is unavailable; the action budget cannot be checked.");
                return (HealingRunOutcome.Failed, false);
            }

            if (!allowed)
            {
                _logger.LogError(
                    "Condition {Condition}: action budget exhausted ({Max} actions per {Window}). Manual intervention required.",
                    name, _options.MaxActionsPerWindow, _options.ActionBudgetWindow);
                return (HealingRunOutcome.BudgetExhausted, false);
            }

            // Disposed when the step ends, or, if an action or a verifier was abandoned at its timeout and still runs,
            // once it ends: its services must not be disposed under it.
            await using var scope = new StepScope(_scopeFactory.CreateAsyncScope(), _logger);
            CurrentStepScope.Value = scope;
            var context = new HealingContext(
                name, trigger, i, step.Name, _timeProvider.GetUtcNow(), scope.ServiceProvider, tracker.Snapshot)
            {
                ClearAfter = tracker.Definition.ClearAfter,
                IsClusterPlan = plan.Scope == PlanScope.Cluster,
            };

            _logger.LogWarning(
                "Condition {Condition}: executing step {Step}/{Total} {Action}.",
                name, i + 1, planSteps.Count, step.Name);
            RecordAction(name, step.Name, dryRun: false);

            // A switch that was already on without any condition owning it was turned on by a person: the step
            // must not take it over, or the condition clearing would turn it off against their decision. (A cluster
            // plan's switch is an application switch, whose owner the store records: see HomeostatSwitches.)
            var cluster = plan.Scope == PlanScope.Cluster;
            var turnedOnByHand = !cluster && step.SwitchToResetOnClear is { } resettable && _switches.IsOn(resettable)
                && !_ownership.IsOwned(resettable) && _switches.GetShared(resettable)?.Condition is null;

            var (executed, error) = await TryExecuteAsync(context, step, plan, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            context.ActionCompletedAt = _timeProvider.GetUtcNow();

            if (!executed)
            {
                // The action timed out: part of it may have taken effect (a request accepted, then no answer), so its
                // compensating action runs too, and the escalation does not start from a half-done change. An action
                // that threw is not compensated: it may have refused on purpose (an origin someone else disabled).
                bool? undoneAfterFailure = null;
                if (error is TimeoutException && step.ResolveUndo is { } undoAfterFailure && !cancellationToken.IsCancellationRequested)
                {
                    undoneAfterFailure = await TryUndoAsync(context, step, undoAfterFailure, plan, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                }

                Report(results, new HealingStepResult
                {
                    RunId = runId,
                    ConditionName = name,
                    Step = i,
                    Name = step.Name,
                    Succeeded = false,
                    Error = error,
                    UndoSucceeded = undoneAfterFailure,
                });
                continue;
            }

            if (step.SwitchToResetOnClear is { } switchName && !turnedOnByHand)
            {
                // Only switches Homeostat actually turned on are turned off when the condition clears.
                if (!cluster)
                {
                    _ownership.Claim(switchName, name);
                }

                if (step.MaxHold is { } maxHold)
                {
                    // Not the run's token: the run ends (and a cluster run releases its lease) long before the hold does.
                    LimitHold(name, switchName, cluster, maxHold, _hostStopping);
                }

                // The condition may have cleared while the step ran (after the engine released its switches): then
                // nobody would turn this one off.
                if (tracker.Snapshot().Status != ConditionStatus.Active)
                {
                    var released = cluster ? await TryReleaseSharedAsync(name).ConfigureAwait(false) : ReleaseLocal(name);
                    foreach (var free in released)
                    {
                        _logger.LogInformation("Condition {Condition} cleared while step {Step} ran: switch {Switch} turned off.", name, step.Name, free);
                    }
                }
            }

            var verifierType = plan.VerifierType ?? typeof(ConditionClearedVerifier);
            var verifier = (IHealingVerifier)scope.ServiceProvider.GetRequiredService(verifierType);
            var timeout = step.VerificationTimeout ?? _options.DefaultVerificationTimeout;
            VerificationOutcome verification;
            Exception? verificationError = null;
            try
            {
                // The verifier gets its timeout; Homeostat waits at most twice as long, plus ClearAfter (the default
                // verifier waits for a recovery in progress to hold), so one that never returns cannot hold the run,
                // its concurrency slot or the shutdown.
                var verificationLimitSpan = Guardrails.TimerLimits.Clamp(timeout + timeout + tracker.Definition.ClearAfter);
                using var verificationDeadline = new CancellationTokenSource(verificationLimitSpan, _timeProvider);
                using var verificationLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, verificationDeadline.Token);
                var what = $"the verification after {step.Name}";
                var key = $"{name}/{what}";
                if (_abandoned.TryGetValue(key, out var stuck) && !stuck.IsCompleted)
                {
                    // Like an action: a verifier abandoned earlier and still running is not started again.
                    throw new TimeoutException($"The verifier after '{step.Name}' is not started: its previous call, abandoned at its timeout, is still running.");
                }

                // The verifier's token is canceled at Homeostat's own limit, so one that honours it is not left running.
                var verifyToken = verificationLimit.Token;
                var verifying = Task.Run(() => verifier.VerifyAsync(context, timeout, verifyToken), CancellationToken.None);
                try
                {
                    verification = await verifying.WaitAsync(verificationLimit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !verifying.IsCompleted)
                {
                    // Stopping (or the lease lost) while it still runs: its services stay until it ends.
                    CurrentStepScope.Value?.Hold(verifying);
                    throw;
                }
                catch (OperationCanceledException) when (verificationDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (!await EndsSoonAsync(verifying).ConfigureAwait(false))
                    {
                        _abandoned[key] = verifying;
                        _ = verifying.ContinueWith(t => _abandoned.TryRemove(new KeyValuePair<string, Task>(key, t)), CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        Abandon(verifying, name, what);
                    }

                    throw new TimeoutException($"The verifier after '{step.Name}' did not return within {verificationLimitSpan} (twice its timeout, plus ClearAfter).");
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A broken verifier must not hide the step: the result cannot be judged, so stop escalating.
                _logger.LogError(ex, "Condition {Condition}: verification after {Action} threw an exception.", name, step.Name);
                verification = VerificationOutcome.Inconclusive;
                verificationError = ex;
            }

            if (verification is not (VerificationOutcome.Healed or VerificationOutcome.NotHealed or VerificationOutcome.Inconclusive))
            {
                // For example default(VerificationOutcome): not evidence either way, so neither stop as healed nor escalate.
                _logger.LogError(
                    "Condition {Condition}: the verifier after {Action} returned {Value}, which is not a VerificationOutcome; treated as Inconclusive.",
                    name, step.Name, (int)verification);
                verification = VerificationOutcome.Inconclusive;
            }

            bool? undone = null;
            if (verification == VerificationOutcome.NotHealed && step.ResolveUndo is { } resolveUndo)
            {
                undone = await TryUndoAsync(context, step, resolveUndo, plan, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }

            Report(results, new HealingStepResult
            {
                RunId = runId,
                ConditionName = name,
                Step = i,
                Name = step.Name,
                Succeeded = true,
                Verification = verification,
                Error = verificationError,
                UndoSucceeded = undone,
            });

            switch (verification)
            {
                case VerificationOutcome.Healed:
                    _logger.LogInformation("Condition {Condition}: healed by {Action}.", name, step.Name);
                    return (HealingRunOutcome.Healed, false);

                case VerificationOutcome.Inconclusive:
                    _logger.LogWarning(
                        "Condition {Condition}: verification after {Action} was inconclusive; not escalating further.",
                        name, step.Name);
                    return (HealingRunOutcome.Inconclusive, false);

                case VerificationOutcome.NotHealed:
                    _logger.LogWarning("Condition {Condition}: {Action} did not heal the condition.", name, step.Name);
                    break;
            }
        }

        _logger.LogError(
            "Condition {Condition}: all {Count} healing steps were tried without success. Manual intervention required.",
            name, planSteps.Count);
        return (HealingRunOutcome.NotHealed, false);
    }

    /// <summary>Runs the compensating action: <c>true</c> or <c>false</c> as it completed or failed, <c>null</c> when it was not run.</summary>
    private async Task<bool?> TryUndoAsync(
        HealingContext context,
        HealingStep step,
        Func<IServiceProvider, IHealingAction> resolveUndo,
        HealingPlan plan,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // The compensating action reverts what this run just changed: the pause, the maintenance windows and the budget,
        // which keep Homeostat from making new changes, never block it, or the application would stay degraded by a
        // change that did not help. It still counts in the budget, best effort.
        try
        {
            var now = _timeProvider.GetUtcNow();
            await SharedControl.Bounded(
                () => _store.TryConsumeBudgetAsync(now, _options.MaxActionsPerWindow, _options.ActionBudgetWindow, cancellationToken),
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Condition {Condition}: the compensating action could not be counted in the budget.", context.ConditionName);
        }

        var timeout = step.ActionTimeout ?? _options.DefaultActionTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        _logger.LogWarning("Condition {Condition}: {Action} did not heal or failed; compensating with {Undo}.", context.ConditionName, step.Name, step.UndoName);
        try
        {
            var undo = resolveUndo(services);
            await RunBoundedAsync(() => undo.ExecuteAsync(context, linkedCts.Token), linkedCts.Token, context.ConditionName, step.UndoName ?? "undo").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Condition {Condition}: compensating action {Undo} failed.", context.ConditionName, step.UndoName);
            return false;
        }
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
        var request = new PendingApproval
        {
            Id = Guid.NewGuid().ToString("N"),
            ConditionName = name,
            Step = index,
            StepName = step.Name,
            RequestedAt = requestedAt,
            ExpiresAt = requestedAt + timeout,
            TriggeringSignal = trigger,
        };
        Task<(ApprovalStatus Status, string? By)> decision;
        try
        {
            decision = await _approvals.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Nobody could answer a request the store does not have: fail closed, the step does not run.
            LogStoreFailure(name, ex, "Condition {Condition}: the store is unavailable; the approval cannot be requested.");
            return ApprovalStatus.Canceled;
        }

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
                await _approvals.CancelAsync(request.Id).ConfigureAwait(false);
            }
            else if (!decision.IsCompleted)
            {
                await _approvals.ExpireAsync(request.Id).ConfigureAwait(false);
            }

            delayCts.Cancel();
        }

        var (status, by) = await decision.ConfigureAwait(false);
        await _approvals.ForgetAsync(request.Id).ConfigureAwait(false);

        // Notify before anything else, so every request in the audit trail is closed, even on shutdown.
        var resolution = new ApprovalResolution { Request = request, Status = status, ResolvedBy = by, ResolvedAt = _timeProvider.GetUtcNow() };
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
        var timeout = step.ActionTimeout ?? _options.DefaultActionTimeout;

        // The timeout follows the TimeProvider, so it can be tested with simulated time.
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var action = step.Resolve(services);
            await RunBoundedAsync(() => action.ExecuteAsync(context, linkedCts.Token), linkedCts.Token, context.ConditionName, step.Name).ConfigureAwait(false);
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

    /// <summary>
    /// Runs an action off the run's thread and stops waiting for it at the deadline, even if it ignores its token: a
    /// stuck action (a blocking call, a lost cancellation) cannot hold the run, the concurrency slot or the shutdown.
    /// </summary>
    private async Task RunBoundedAsync(Func<Task> work, CancellationToken deadline, string condition, string what)
    {
        // An action abandoned earlier and still running is not started again: each abandoned call keeps a thread (it may
        // be blocked), and a plan that runs again after every cooldown would otherwise pile them up until the
        // application runs out of threads.
        var key = $"{condition}/{what}";
        if (_abandoned.TryGetValue(key, out var stuck) && !stuck.IsCompleted)
        {
            throw new InvalidOperationException($"{what} is not started: its previous call, abandoned at its timeout, is still running.");
        }

        var task = Task.Run(work, CancellationToken.None);
        try
        {
            await task.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // An action that honours its token ends a moment after the cancellation: only one still running after that
            // moment is abandoned.
            if (!await EndsSoonAsync(task).ConfigureAwait(false))
            {
                _abandoned[key] = task;
                _ = task.ContinueWith(t => _abandoned.TryRemove(new KeyValuePair<string, Task>(key, t)), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Abandon(task, condition, what);
            }

            throw;
        }
    }

    // How long a call that honours its cancellation token gets to end (real time) before it counts as abandoned.
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromMilliseconds(250);

    private static async Task<bool> EndsSoonAsync(Task task)
    {
        if (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(CancellationGrace)).ConfigureAwait(false);
        }

        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.IsCompleted;
    }

    private readonly ConcurrentDictionary<string, Task> _abandoned = new(StringComparer.Ordinal);

    private CancellationToken _hostStopping;

    private static readonly AsyncLocal<StepScope?> CurrentStepScope = new();

    /// <summary>The services of one step, kept until the calls it abandoned have ended.</summary>
    private sealed class StepScope(AsyncServiceScope scope, ILogger logger) : IAsyncDisposable
    {
        private readonly List<Task> _held = [];

        public IServiceProvider ServiceProvider => scope.ServiceProvider;

        public void Hold(Task task)
        {
            lock (_held)
            {
                _held.Add(task);
            }
        }

        public ValueTask DisposeAsync()
        {
            Task[] held;
            lock (_held)
            {
                held = [.. _held];
            }

            if (held.All(t => t.IsCompleted))
            {
                return scope.DisposeAsync();
            }

            _ = Task.WhenAll(held).ContinueWith(
                async _ =>
                {
                    try
                    {
                        await scope.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Disposing the services of an abandoned step failed.");
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return ValueTask.CompletedTask;
        }
    }

    // Claims of switches with HoldAtMost, numbered per condition and switch.
    private readonly ConcurrentDictionary<string, long> _holds = new(StringComparer.Ordinal);

    private void Abandon(Task task, string condition, string what)
    {
        CurrentStepScope.Value?.Hold(task);
        // Observed, so its eventual failure is not an unobserved task exception.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _logger.LogError(
            "Condition {Condition}: {What} did not stop when canceled and is still running; Homeostat no longer waits for it. "
            + "Pass the cancellation token to every call it makes.", condition, what);
    }

    /// <summary>Turns off the application switches this condition's cluster plan turned on here; a store failure is logged.</summary>
    private async Task<IReadOnlyList<string>> TryReleaseSharedAsync(string condition)
    {
        try
        {
            return await _switches.ReleaseSharedAsync(condition, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The step worked: a store failure here must not turn a healed run into a failed one.
            _logger.LogError(ex, "Condition {Condition} cleared, but its application switches could not be turned off in the store.", condition);
            return [];
        }
    }

    /// <summary>Turns the switch off after its longest hold, if the condition still holds it then (see HoldAtMost).</summary>
    private void LimitHold(string condition, string switchName, bool cluster, TimeSpan hold, CancellationToken stoppingToken)
    {
        // This claim only: a later run of the same condition that claims the switch again starts its own hold, and this
        // timer must not cut it short.
        var claimKey = $"{condition}/{switchName}";
        var claim = _holds.AddOrUpdate(claimKey, 1, static (_, n) => n + 1);
        var claimedAt = cluster ? _switches.GetShared(switchName)?.At : null;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(hold, _timeProvider, stoppingToken).ConfigureAwait(false);
                if (!_holds.TryGetValue(claimKey, out var current) || current != claim)
                {
                    return;
                }

                var released = cluster
                    ? await _switches.ReleaseOwnedAsync(switchName, condition, CancellationToken.None, claimedAt).ConfigureAwait(false)
                    : _ownership.Release(condition, switchName) && _switches.TurnOffLocal(switchName);
                if (released)
                {
                    _logger.LogWarning(
                        "Condition {Condition}: switch {Switch} turned off after being held for {Hold} (HoldAtMost); the plan runs again after its cooldown if the condition is still active.",
                        condition, switchName, hold);
                }
            }
            catch (OperationCanceledException)
            {
                // The application is stopping.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Condition {Condition}: switch {Switch} could not be turned off at the end of its hold.", condition, switchName);
            }
        }, CancellationToken.None);
    }

    /// <summary>Turns off the instance switches only this condition held; returns them.</summary>
    private IReadOnlyList<string> ReleaseLocal(string condition)
    {
        var released = new List<string>();
        foreach (var free in _ownership.Release(condition))
        {
            if (_switches.TurnOffLocal(free))
            {
                released.Add(free);
            }
        }

        return released;
    }

    private void Report(List<HealingStepResult> results, HealingStepResult result)
    {
        results.Add(result);
        _notifier.Notify(o => o.OnStepCompleted(result));
    }

    /// <summary>
    /// Whether runs emit traces and metrics. Off in a replay, whose simulated runs must not show up in the process's
    /// telemetry (the instruments are static, shared by every engine in the process).
    /// </summary>
    internal bool TelemetryEnabled { get; set; } = true;

    private void RecordAction(string condition, string step, bool dryRun)
    {
        if (TelemetryEnabled)
        {
            HomeostatDiagnostics.Actions.Add(
                1,
                new KeyValuePair<string, object?>("condition", condition),
                new KeyValuePair<string, object?>("action", step),
                new KeyValuePair<string, object?>("dry_run", dryRun));
        }
    }
}

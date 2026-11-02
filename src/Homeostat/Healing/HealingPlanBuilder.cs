using Homeostat.Healing.Actions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Healing;

/// <summary>Fluent builder for a <see cref="HealingPlan"/>.</summary>
/// <remarks>
/// <c>With</c> and <c>Then</c> are interchangeable: they both append a step. Use <c>With</c> for the first
/// step and <c>Then</c> for escalation steps to make the plan read naturally.
/// </remarks>
public sealed class HealingPlanBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<HealingStep> _steps = [];
    private Type? _verifierType;
    private TimeSpan? _verificationTimeout;
    private TimeSpan? _actionTimeout;
    private TimeSpan? _cooldown;
    private PlanScope _scope = PlanScope.Instance;
    private double _cooldownBackoff = 1;
    private TimeSpan? _maxCooldown;
    private int? _adaptiveMinimumAttempts;

    internal HealingPlanBuilder(IServiceCollection services) => _services = services;

    // ---------------------------------------------------------------- class-based actions

    /// <summary>Appends an action class. Registers <typeparamref name="TAction"/> as transient if not already registered.</summary>
    public HealingPlanBuilder With<TAction>()
        where TAction : class, IHealingAction
    {
        _services.TryAddTransient<TAction>();
        return AddStep(typeof(TAction).Name, static sp => sp.GetRequiredService<TAction>());
    }

    /// <summary>Escalation step: runs only if the previous action failed (threw or timed out) or was verified as <see cref="VerificationOutcome.NotHealed"/>.</summary>
    public HealingPlanBuilder Then<TAction>()
        where TAction : class, IHealingAction
        => With<TAction>();

    // ---------------------------------------------------------------- inline actions

    /// <summary>Appends an inline asynchronous action.</summary>
    /// <param name="name">Name used in logs, metrics and run results.</param>
    /// <param name="action">The action. Use <see cref="HealingContext.Services"/> to resolve services.</param>
    public HealingPlanBuilder With(string name, Func<HealingContext, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        return AddStep(name, _ => new DelegateHealingAction(action));
    }

    /// <inheritdoc cref="With(string, Func{HealingContext, CancellationToken, Task})"/>
    public HealingPlanBuilder Then(string name, Func<HealingContext, CancellationToken, Task> action)
        => With(name, action);

    /// <summary>Appends an inline synchronous action that receives the step's services.</summary>
    /// <remarks>
    /// A lambda returning a <see cref="ValueTask"/> also binds here and is not awaited: write
    /// <c>async sp =&gt; await ...</c> so it binds to the <see cref="Task"/> overload instead.
    /// </remarks>
    /// <example><code>.With("renew-client", sp => sp.GetRequiredService&lt;FeedClient&gt;().Renew())</code></example>
    public HealingPlanBuilder With(string name, Action<IServiceProvider> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return With(name, (context, _) =>
        {
            action(context.Services);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="With(string, Action{IServiceProvider})"/>
    public HealingPlanBuilder Then(string name, Action<IServiceProvider> action)
        => With(name, action);

    /// <summary>Appends an inline asynchronous action that receives the step's services.</summary>
    /// <example><code>.With("renew-client", sp => sp.GetRequiredService&lt;FeedClient&gt;().RenewAsync())</code></example>
    public HealingPlanBuilder With(string name, Func<IServiceProvider, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return With(name, (context, _) => action(context.Services));
    }

    /// <inheritdoc cref="With(string, Func{IServiceProvider, Task})"/>
    public HealingPlanBuilder Then(string name, Func<IServiceProvider, Task> action)
        => With(name, action);

    // ---------------------------------------------------------------- built-in actions

    /// <summary>
    /// Appends a step that restarts the hosted service <typeparamref name="TService"/> (registered with
    /// <c>AddHostedService</c>). Derive background workers from <see cref="RestartableBackgroundService"/>: the restart
    /// then happens inside the service and the host does not notice. A plain <see cref="BackgroundService"/> is refused
    /// (the step fails), because stopping it outside a shutdown makes the generic host stop the application, unless the
    /// host is configured with <c>BackgroundServiceExceptionBehavior.Ignore</c>. Other hosted services are stopped and
    /// started again.
    /// </summary>
    public HealingPlanBuilder RestartHostedService<TService>()
        where TService : class, IHostedService
        => AddStep(
            $"Restart {typeof(TService).Name}",
            static sp => new RestartHostedServiceAction<TService>(
                sp.GetServices<IHostedService>(),
                sp,
                sp.GetRequiredService<ILogger<RestartHostedServiceAction<TService>>>()));

    /// <summary>
    /// Appends a step that stops the application with <paramref name="exitCode"/>, so that the orchestrator or service
    /// manager (Kubernetes, Azure App Service, systemd, Docker with a restart policy) starts a fresh process: the last
    /// resort for a state that only a restart clears (leaked memory, a corrupted cache, a stuck native library).
    /// It requires approval by default, and <see cref="RestartGuard"/> refuses it when this instance started less than
    /// <see cref="RestartLimits.MinimumUptime"/> ago or the application already restarted too often (3 times per hour by
    /// default, counted in the store): the plan then escalates instead of looping.
    /// </summary>
    /// <param name="exitCode">Process exit code; defaults to 1, so "restart on failure" policies apply too.</param>
    /// <param name="requireApproval">Whether a person approves the restart first; defaults to <c>true</c>.</param>
    /// <param name="limits">Minimum uptime and restarts per window; defaults to <see cref="RestartLimits.Default"/>.</param>
    /// <remarks>
    /// The step ends the run: put it last, steps after it never run. Keep the store persistent (<c>UseFileStore</c>,
    /// <c>UseRedis</c>), so the restart count and the cooldown survive the new process.
    /// </remarks>
    /// <example>
    /// <code>
    /// .AddCondition("memory-leak", c => c.ForSource("memory").WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(30))
    ///     .Heal(p => p.CompactMemoryCache(0.5).RestartApplication()))
    /// </code>
    /// </example>
    public HealingPlanBuilder RestartApplication(int exitCode = 1, bool requireApproval = true, RestartLimits? limits = null)
    {
        var effective = limits ?? RestartLimits.Default;
        effective.Validate();

        AddStep("Restart the application", sp => new RestartApplicationAction(
            sp.GetService<IHostApplicationLifetime>(),
            effective,
            exitCode,
            sp.GetRequiredService<ILogger<RestartApplicationAction>>()));
        return requireApproval ? RequireApproval() : this;
    }

    /// <summary>
    /// Appends a step that reloads <see cref="IConfiguration"/> from all its providers.
    /// Components that read options through <c>IOptionsMonitor</c> see the new values.
    /// </summary>
    public HealingPlanBuilder ReloadConfiguration()
        => AddStep(
            "Reload configuration",
            static sp => new ReloadConfigurationAction(
                sp.GetService<IConfiguration>(),
                sp.GetRequiredService<ILogger<ReloadConfigurationAction>>()));

    /// <summary>
    /// Appends a step that turns on a named switch in <see cref="IHomeostatSwitches"/>.
    /// Your code checks the switch to change behavior, for example to serve cached data.
    /// </summary>
    /// <param name="switchName">Name of the switch.</param>
    /// <param name="turnOffWhenCleared">
    /// When <c>true</c>, the switch is turned off automatically when the condition clears. Make sure the switch
    /// itself does not make the condition clear (for example by hiding the failing dependency from its health
    /// check), otherwise the condition flaps; <see cref="Conditions.ConditionBuilder.ClearAfter"/> helps.
    /// </param>
    public HealingPlanBuilder TurnOnSwitch(string switchName, bool turnOffWhenCleared = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(switchName);
        _steps.Add(new HealingStep(
            $"Turn on switch '{switchName}'",
            sp => new TurnOnSwitchAction(
                sp.GetRequiredService<IHomeostatSwitches>(),
                switchName,
                sp.GetRequiredService<ILogger<TurnOnSwitchAction>>()))
        {
            SwitchToResetOnClear = turnOffWhenCleared ? switchName : null,
        });
        return this;
    }

    // ---------------------------------------------------------------- approval

    /// <summary>
    /// Human in the loop: the step added just before this call runs only after a person approves it through
    /// <see cref="IHomeostatApprovals"/> (or the <c>Homeostat.AspNetCore</c> endpoints). If the request is rejected
    /// or not answered within <paramref name="timeout"/>, the run stops with <see cref="HealingRunOutcome.NotApproved"/>.
    /// </summary>
    /// <param name="timeout">How long to wait; <c>null</c> means <see cref="HomeostatOptions.DefaultApprovalTimeout"/>.</param>
    /// <example>
    /// <code>
    /// .Heal(p => p
    ///     .With&lt;RenewFeedClientAction&gt;()
    ///     .Then&lt;SwitchToBackupProviderAction&gt;().RequireApproval(TimeSpan.FromMinutes(10)))
    /// </code>
    /// </example>
    public HealingPlanBuilder RequireApproval(TimeSpan? timeout = null)
    {
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("RequireApproval applies to the previous step: add a step first.");
        }

        if (timeout is { } value)
        {
            Positive(value, nameof(timeout));
        }

        var step = _steps[^1];
        step.RequiresApproval = true;
        step.ApprovalTimeout = timeout;
        return this;
    }

    // ---------------------------------------------------------------- verification and pacing

    /// <summary>Use a custom verifier instead of waiting for the condition to clear.</summary>
    public HealingPlanBuilder VerifyWith<TVerifier>()
        where TVerifier : class, IHealingVerifier
    {
        _services.TryAddTransient<TVerifier>();
        _verifierType = typeof(TVerifier);
        return this;
    }

    /// <summary>Time allowed for verification after each action (default <see cref="HomeostatOptions.DefaultVerificationTimeout"/>).</summary>
    public HealingPlanBuilder VerificationTimeout(TimeSpan timeout)
    {
        _verificationTimeout = Positive(timeout, nameof(timeout));
        return this;
    }

    /// <summary>Maximum duration of each action (default <see cref="HomeostatOptions.DefaultActionTimeout"/>).</summary>
    public HealingPlanBuilder ActionTimeout(TimeSpan timeout)
    {
        _actionTimeout = Positive(timeout, nameof(timeout));
        return this;
    }

    /// <summary>Minimum time between the end of a run and the start of the next run of this plan.</summary>
    public HealingPlanBuilder Cooldown(TimeSpan cooldown)
    {
        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown cannot be negative.");
        }

        _cooldown = cooldown;
        return this;
    }

    /// <summary>
    /// Grows the cooldown after every run in a row that does not heal: cooldown × <paramref name="multiplier"/>^failures,
    /// up to <paramref name="max"/>. A healed run or the condition clearing resets it.
    /// </summary>
    /// <param name="multiplier">Growth factor, at least 1.</param>
    /// <param name="max">Upper bound of the cooldown.</param>
    /// <example><code>.Cooldown(TimeSpan.FromMinutes(5)).CooldownBackoff(2, TimeSpan.FromHours(1))</code></example>
    public HealingPlanBuilder CooldownBackoff(double multiplier, TimeSpan max)
    {
        if (!(multiplier >= 1) || double.IsInfinity(multiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "The multiplier must be a finite number of at least 1.");
        }

        _cooldownBackoff = multiplier;
        _maxCooldown = Positive(max, nameof(max));
        return this;
    }

    /// <summary>
    /// Runs the plan on one instance at a time across the cluster, with a shared cooldown, instead of on every
    /// instance that sees the condition. Needs a distributed coordinator and store (for example <c>Homeostat.Redis</c>)
    /// to have an effect beyond the process.
    /// </summary>
    public HealingPlanBuilder OncePerCluster()
    {
        _scope = PlanScope.Cluster;
        return this;
    }

    // ---------------------------------------------------------------- learning

    /// <summary>
    /// Lets the plan learn which step works: before each run, the steps are ordered by how often each one healed this
    /// condition in the run history (successes over attempts, smoothed so that one lucky run does not decide), best
    /// first, ties in the declared order. Steps move only among the steps between two approval steps: a step that needs
    /// approval never moves ahead of the cheaper ones before it. Only steps tried at least
    /// <paramref name="minimumAttempts"/> times each are reordered, among their own positions; a step with fewer
    /// attempts (a new one, or one rarely reached) keeps its declared position. The history is the run history of
    /// the store (<see cref="HomeostatOptions.RunHistorySize"/> runs), shared by every instance with a shared store.
    /// </summary>
    /// <param name="minimumAttempts">Attempts a step needs before it is reordered; defaults to 5.</param>
    /// <example>
    /// <code>
    /// .Heal(p => p
    ///     .With&lt;RenewClientAction&gt;()
    ///     .Then&lt;FlushDnsCacheAction&gt;()
    ///     .Then&lt;ResetConnectionPoolAction&gt;()
    ///     .Adaptive())          // whichever usually fixes it goes first
    /// </code>
    /// </example>
    public HealingPlanBuilder Adaptive(int minimumAttempts = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAttempts, 1);
        _adaptiveMinimumAttempts = minimumAttempts;
        return this;
    }

    // ---------------------------------------------------------------- compensation

    /// <summary>
    /// Compensating action for the step added just before this call: it runs when the step's action completed but
    /// verification says <see cref="VerificationOutcome.NotHealed"/>, before escalating, so a change that did not help
    /// is rolled back. It does not consume the action budget.
    /// </summary>
    /// <typeparam name="TAction">The compensating action.</typeparam>
    /// <example><code>.Then&lt;SwitchToBackupProviderAction&gt;().UndoWith&lt;SwitchToPrimaryProviderAction&gt;()</code></example>
    public HealingPlanBuilder UndoWith<TAction>()
        where TAction : class, IHealingAction
    {
        _services.TryAddTransient<TAction>();
        return SetUndo(typeof(TAction).Name, static sp => sp.GetRequiredService<TAction>());
    }

    /// <summary>Inline compensating action for the step added just before this call (see <see cref="UndoWith{TAction}"/>).</summary>
    /// <param name="name">Name used in logs.</param>
    /// <param name="action">The compensating action.</param>
    public HealingPlanBuilder UndoWith(string name, Func<HealingContext, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        return SetUndo(name, _ => new DelegateHealingAction(action));
    }

    internal HealingPlan Build(string conditionName)
    {
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException($"The healing plan for condition '{conditionName}' has no actions.");
        }

        return new HealingPlan(
            conditionName, _steps.ToArray(), _verifierType, _verificationTimeout, _actionTimeout, _cooldown,
            _scope, _cooldownBackoff, _maxCooldown)
        {
            AdaptiveMinimumAttempts = _adaptiveMinimumAttempts,
        };
    }

    private HealingPlanBuilder SetUndo(string name, Func<IServiceProvider, IHealingAction> resolve)
    {
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("UndoWith applies to the previous step: add a step first.");
        }

        _steps[^1].UndoName = name;
        _steps[^1].ResolveUndo = resolve;
        return this;
    }

    private HealingPlanBuilder AddStep(string name, Func<IServiceProvider, IHealingAction> resolve)
    {
        _steps.Add(new HealingStep(name, resolve));
        return this;
    }

    private static TimeSpan Positive(TimeSpan value, string name)
        => Guardrails.TimerLimits.IsValid(value)
            ? value
            : throw new ArgumentOutOfRangeException(name, "Value must be positive and at most about 49 days.");
}

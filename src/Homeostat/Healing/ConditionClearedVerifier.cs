using Homeostat.Conditions;
using Microsoft.Extensions.Options;

namespace Homeostat.Healing;

/// <summary>
/// Default verifier: polls the condition until it clears or the timeout expires.
/// <list type="bullet">
/// <item><description>Condition cleared → <see cref="VerificationOutcome.Healed"/>.</description></item>
/// <item><description>Timeout, with fresh signals still showing the problem → <see cref="VerificationOutcome.NotHealed"/>.</description></item>
/// <item><description>Timeout, with no signal received after the action → <see cref="VerificationOutcome.Inconclusive"/>.</description></item>
/// </list>
/// When the condition uses <c>ClearAfter</c> and recovery is in progress at the timeout, the verifier keeps waiting
/// for up to <c>ClearAfter</c> more, so a fix that works is not mistaken for one that does not.
/// </summary>
public sealed class ConditionClearedVerifier : IHealingVerifier
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;

    /// <summary>Created by dependency injection.</summary>
    /// <param name="timeProvider">Clock used for polling and timeouts.</param>
    /// <param name="options">Provides <see cref="HomeostatOptions.VerificationPollInterval"/>.</param>
    public ConditionClearedVerifier(TimeProvider timeProvider, IOptions<HomeostatOptions> options)
    {
        _timeProvider = timeProvider;
        _pollInterval = options.Value.VerificationPollInterval;
    }

    /// <inheritdoc />
    public async Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var actionCompletedAt = context.ActionCompletedAt ?? context.StartedAt;
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            var state = context.GetConditionState();
            if (state.Status == ConditionStatus.Inactive)
            {
                return VerificationOutcome.Healed;
            }

            var now = _timeProvider.GetUtcNow();
            if (now >= deadline && state.ClearingSince is not null && now < deadline + context.ClearAfter)
            {
                // Recovering: give hysteresis the time to confirm it.
            }
            else if (now >= deadline)
            {
                return state.LastSignalAt is { } last && last > actionCompletedAt
                    ? VerificationOutcome.NotHealed
                    : VerificationOutcome.Inconclusive;
            }

            var remaining = (now < deadline ? deadline : deadline + context.ClearAfter) - now;
            var delay = remaining < _pollInterval ? remaining : _pollInterval;
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}

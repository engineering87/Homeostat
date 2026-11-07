using System.Text.Json;
using System.Text.Json.Serialization;
using Homeostat.Healing;
using Homeostat.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat.Redis;

/// <summary>
/// <see cref="IHomeostatStore"/> on Redis: plan state in hashes, the budget in a sorted set updated by a script
/// (atomic across instances), the history in a capped list.
/// </summary>
internal sealed class RedisHomeostatStore : IHomeostatStore
{

    private static readonly RedisValue[] PlanFields = ["ended", "failures", "held", "by"];

    // KEYS[1] = plan hash; ARGV = ended (ms, -1 keep), by ('' keep), failures (0 keep, 1 increment, 2 reset),
    // held (-1 keep, 0, 1), precondition on 'by' ('' none), retention (ms, not applied while held). Returns ended,
    // failures, held, by.
    private const string UpdatePlanScript = """
        local state = redis.call('HMGET', KEYS[1], 'ended', 'failures', 'held', 'by')
        local by = state[4] or ''
        if ARGV[5] ~= '' and by ~= ARGV[5] then
          return state
        end
        local ended = state[1] or '0'
        local failures = tonumber(state[2] or '0')
        local held = state[3] or '0'
        if tonumber(ARGV[1]) >= 0 then ended = ARGV[1] end
        if ARGV[2] ~= '' then by = ARGV[2] end
        if ARGV[3] == '1' then failures = failures + 1 elseif ARGV[3] == '2' then failures = 0 end
        if ARGV[4] ~= '-1' then held = ARGV[4] end
        redis.call('HSET', KEYS[1], 'ended', ended, 'failures', failures, 'held', held, 'by', by)
        -- A held plan is kept until the hold ends (the condition clears or a person releases it), like the file store
        -- does: expiring it would silently undo a person's rejection.
        if held == '1' then redis.call('PERSIST', KEYS[1]) else redis.call('PEXPIRE', KEYS[1], ARGV[6]) end
        return { ended, tostring(failures), held, by }
        """;

    // KEYS[1] = budget set; ARGV = now (ms), window (ms), limit, member.
    private const string ConsumeBudgetScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', tonumber(ARGV[1]) - tonumber(ARGV[2]))
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[3]) then
          return 0
        end
        redis.call('ZADD', KEYS[1], ARGV[1], ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    private readonly RedisConnection _redis;
    private readonly RedisStoreOptions _options;
    private readonly string _budgetKey;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private int _pauseUnreadable;

    public RedisHomeostatStore(IConnectionMultiplexer redis, IOptions<RedisStoreOptions> options, IOptions<HomeostatOptions> homeostat, TimeProvider? timeProvider = null, ILogger<RedisHomeostatStore>? logger = null)
        : this(() => redis, options, homeostat, timeProvider, logger)
    {
    }

    /// <summary>
    /// Resolves the connection on first use, not when the services are built: connecting can take seconds when Redis is
    /// unreachable, and the application's startup must not wait for it.
    /// </summary>
    internal RedisHomeostatStore(Func<IConnectionMultiplexer> redis, IOptions<RedisStoreOptions> options, IOptions<HomeostatOptions> homeostat, TimeProvider? timeProvider = null, ILogger<RedisHomeostatStore>? logger = null)
    {
        // Kept once resolved; a resolution that failed is tried again after a few seconds, not on every call.
        _redis = new RedisConnection(redis);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _budgetKey = _options.ShareBudget
            ? $"{_options.KeyPrefix}budget"
            : $"{_options.KeyPrefix}budget:{homeostat.Value.InstanceId}";
    }

    private IDatabase Db => _redis.Get().GetDatabase();

    public async ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default)
    {
        var values = await Db.HashGetAsync(PlanKey(key), PlanFields).ConfigureAwait(false);
        return ToState(values);
    }

    public async ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(
            UpdatePlanScript,
            [PlanKey(key)],
            [
                update.LastRunEndedAt?.ToUnixTimeMilliseconds() ?? -1,
                update.LastRunBy ?? string.Empty,
                (int)update.Failures,
                update.Held is { } held ? (held ? 1 : 0) : -1,
                update.OnlyIfLastRunBy ?? string.Empty,
                (long)_options.PlanStateRetention.TotalMilliseconds,
            ]).ConfigureAwait(false))!;

        return ToState(result.Select(r => (RedisValue)r).ToArray());
    }

    public async ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var result = await Db.ScriptEvaluateAsync(
            ConsumeBudgetScript,
            [_budgetKey],
            [now.ToUnixTimeMilliseconds(), (long)window.TotalMilliseconds, limit, Guid.NewGuid().ToString("N")]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var count = await Db.SortedSetLengthAsync(
            _budgetKey,
            min: now.ToUnixTimeMilliseconds() - (long)window.TotalMilliseconds,
            max: double.PositiveInfinity,
            exclude: Exclude.Start).ConfigureAwait(false);
        return (int)count;
    }

    public async ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maxRuns <= 0)
        {
            return;
        }

        var key = $"{_options.KeyPrefix}runs";
        var batch = Db.CreateBatch();
        var push = batch.ListLeftPushAsync(key, JsonSerializer.Serialize(run, RedisJsonContext.Default.HealingRunRecord));
        var trim = batch.ListTrimAsync(key, 0, Math.Max(0, maxRuns - 1));
        batch.Execute();
        await Task.WhenAll(push, trim).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default)
    {
        if (maxRuns <= 0)
        {
            return [];
        }

        var values = await Db.ListRangeAsync($"{_options.KeyPrefix}runs", 0, maxRuns - 1).ConfigureAwait(false);
        var runs = new List<HealingRunRecord>(values.Length);
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize((string)value!, RedisJsonContext.Default.HealingRunRecord) is { } run)
                {
                    runs.Add(run);
                }
            }
            catch (JsonException)
            {
                // A record written by another version (for example during a rolling upgrade): skip it.
            }
        }

        return runs;
    }

    public bool SupportsConditionHistory => true;

    public bool SupportsDocuments => true;

    public async ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditionEvent);
        if (maxEvents <= 0)
        {
            return;
        }

        var key = $"{_options.KeyPrefix}conditions";
        var batch = Db.CreateBatch();
        var push = batch.ListLeftPushAsync(key, JsonSerializer.Serialize(conditionEvent, RedisJsonContext.Default.ConditionEventRecord));
        var trim = batch.ListTrimAsync(key, 0, Math.Max(0, maxEvents - 1));
        batch.Execute();
        await Task.WhenAll(push, trim).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default)
    {
        if (maxEvents <= 0)
        {
            return [];
        }

        var values = await Db.ListRangeAsync($"{_options.KeyPrefix}conditions", 0, maxEvents - 1).ConfigureAwait(false);
        var events = new List<ConditionEventRecord>(values.Length);
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize((string)value!, RedisJsonContext.Default.ConditionEventRecord) is { } conditionEvent)
                {
                    events.Add(conditionEvent);
                }
            }
            catch (JsonException)
            {
                // Written by another version: skip it.
            }
        }

        return events;
    }

    public async ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = await Db.StringGetAsync($"{_options.KeyPrefix}doc:{key}").ConfigureAwait(false);
        return value.HasValue ? (string?)value : null;
    }

    public async ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(document);
        await Db.StringSetAsync($"{_options.KeyPrefix}doc:{key}", document, _options.DocumentRetention).ConfigureAwait(false);
    }

    public async ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default)
    {
        var batch = Db.CreateBatch();
        var pause = batch.StringGetAsync(ControlKey("pause"));
        var switches = batch.HashGetAllAsync(ControlKey("switches"));
        var approvals = batch.HashGetAllAsync(ControlKey("approvals"));
        var answers = batch.HashGetAllAsync(ControlKey("answers"));
        batch.Execute();
        await Task.WhenAll(pause, switches, approvals, answers).ConfigureAwait(false);

        var answered = (await answers.ConfigureAwait(false)).ToDictionary(e => (string)e.Name!, e => (string?)e.Value, StringComparer.Ordinal);
        var records = new List<ApprovalRecord>();
        var abandoned = new List<RedisValue>();
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in await approvals.ConfigureAwait(false))
        {
            // Unreadable (for example written by a newer version during a rolling upgrade, or without a request): left
            // for its writer. One bad record must not make the whole control state unreadable.
            if (Read(entry.Value, RedisJsonContext.Default.ApprovalRecord) is not { Request: not null } record)
            {
                continue;
            }

            // A request whose instance stopped before collecting the answer: drop it a day after it expired.
            if (record.Request.ExpiresAt < now - TimeSpan.FromDays(1))
            {
                abandoned.Add(entry.Name);
                continue;
            }

            if (answered.TryGetValue(record.Request.Id, out var answer) && answer is not null)
            {
                // An answer this version does not know leaves the request pending: it then expires, and the step does not run.
                var separator = answer.IndexOf('\n', StringComparison.Ordinal);
                if (Enum.TryParse<ApprovalStatus>(separator < 0 ? answer : answer[..separator], ignoreCase: false, out var status)
                    && Enum.IsDefined(status))
                {
                    var by = separator < 0 || separator == answer.Length - 1 ? null : answer[(separator + 1)..];
                    record = new ApprovalRecord { Request = record.Request, Instance = record.Instance, Status = status, ResolvedBy = by };
                }
            }

            records.Add(record);
        }

        if (abandoned.Count > 0)
        {
            await RemoveApprovalsAsync([.. abandoned]).ConfigureAwait(false);
        }

        return new ControlState
        {
            Pause = ReadPause(await pause.ConfigureAwait(false)),
            Switches = (await switches.ConfigureAwait(false))
                .Select(e => Read(e.Value, RedisJsonContext.Default.SwitchState))
                .OfType<SwitchState>()
                .Where(s => s.Name is not null)   // one unreadable record must not make the whole state unreadable
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToArray(),
            Approvals = records.OrderBy(r => r.Request.RequestedAt).ToArray(),
        };
    }

    /// <summary>
    /// The pause, failing closed: a pause record that cannot be read (written by another version, or damaged) counts as
    /// an indefinite pause, never as "not paused", so Homeostat does not act through a pause it cannot see. A person
    /// resumes on purpose (which replaces the record).
    /// </summary>
    private PauseState? ReadPause(RedisValue value)
    {
        if (!value.HasValue)
        {
            Volatile.Write(ref _pauseUnreadable, 0);
            return null;
        }

        if (Read(value, RedisJsonContext.Default.PauseState) is { } pause)
        {
            Volatile.Write(ref _pauseUnreadable, 0);
            return pause;
        }

        // Once until it is readable again: the control state is read every few seconds.
        if (Interlocked.Exchange(ref _pauseUnreadable, 1) == 0)
        {
            _logger.LogCritical(
                "The Homeostat pause record {Key} in Redis cannot be read: Homeostat counts as paused until a person resumes it.",
                (string?)ControlKey("pause"));
        }

        return new PauseState { At = DateTimeOffset.MinValue, By = "unreadable pause record in the store (resume to replace it)" };
    }

    // Decision times are Unix microseconds (exact in a Lua number). A change that makes Homeostat do less (a pause, a
    // switch off, a release) is accepted on a tie, as a deferred change may be written again with the same time; a change
    // that lets it act (a resume, a switch on, a claim) only when strictly later.

    // KEYS[1] = pause, KEYS[2] = decided; ARGV[1] = pause (JSON, '' to resume), ARGV[2] = decided at.
    private const string SetPauseInOrderScript = """
        local latest = tonumber(redis.call('HGET', KEYS[2], 'pause') or '-1')
        local at = tonumber(ARGV[2])
        if (ARGV[1] == '' and at <= latest) or at < latest then
          return 0
        end
        redis.call('HSET', KEYS[2], 'pause', ARGV[2])
        if ARGV[1] == '' then
          redis.call('DEL', KEYS[1])
        else
          redis.call('SET', KEYS[1], ARGV[1])
        end
        return 1
        """;

    // KEYS[1] = switches, KEYS[2] = decided; ARGV[1] = name, ARGV[2] = state (JSON, '' to turn off), ARGV[3] = decided at.
    private const string SetSwitchInOrderScript = """
        local field = 'switch:' .. ARGV[1]
        local latest = tonumber(redis.call('HGET', KEYS[2], field) or '-1')
        local at = tonumber(ARGV[3])
        if (ARGV[2] ~= '' and at <= latest) or at < latest then
          return 0
        end
        redis.call('HSET', KEYS[2], field, ARGV[3])
        redis.call('HDEL', KEYS[2], 'claim:' .. ARGV[1], 'renewed:' .. ARGV[1])   -- no longer a condition's claim
        if ARGV[2] == '' then
          redis.call('HDEL', KEYS[1], ARGV[1])
        else
          redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        end
        return 1
        """;

    // KEYS[1] = switches, KEYS[2] = decided; ARGV[1] = name, ARGV[2] = state (JSON), ARGV[3] = condition, ARGV[4] = the
    // claim's time. Sets the switch only if it is owned by the same condition and not claimed later since (by another
    // instance), or off and not decided about at or after the claim (a late claim, or a renewal of a switch released
    // meanwhile).
    // ARGV[5] = '1' for a renewal (or an abandonment), which only updates a claim still held by ARGV[6] (the instance),
    // and records ARGV[7] (the renewal's time, '' for an abandonment) in 'renewed:{name}'. A renewal is not a decision:
    // the switch's decision time stays the claim's, and a release decided before the renewal is refused.
    private const string ClaimSwitchScript = """
        local field = 'switch:' .. ARGV[1]
        local latest = tonumber(redis.call('HGET', KEYS[2], field) or '-1')
        local at = tonumber(ARGV[4])
        local renewal = ARGV[5] == '1'
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        if current then
          local ok, state = pcall(cjson.decode, current)
          if not ok or type(state) ~= 'table' or state['condition'] ~= ARGV[3] then
            return 0
          end
          if renewal then
            -- The same claim only: the same instance, and the claim's own time (recorded when it was made).
            if state['instance'] ~= ARGV[6] or redis.call('HGET', KEYS[2], 'claim:' .. ARGV[1]) ~= ARGV[4] then
              return 0
            end
          elseif at < latest then
            return 0
          end
        elseif renewal or at <= latest then
          return 0
        end
        -- Times are written as the caller's strings: tostring() would round a Lua number of 16 digits. A renewal is not a
        -- decision (a person's later "off" must win over it): it only records when the claim was last renewed, which an
        -- orphan release checks.
        if renewal then
          redis.call('HSET', KEYS[2], 'renewed:' .. ARGV[1], ARGV[7] ~= '' and ARGV[7] or '-1')
        else
          if at > latest then
            redis.call('HSET', KEYS[2], field, ARGV[4])
          end
          redis.call('HSET', KEYS[2], 'claim:' .. ARGV[1], ARGV[4], 'renewed:' .. ARGV[1], ARGV[4])
        end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        return 1
        """;

    private static long Micros(DateTimeOffset at) => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    public async ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        var json = pause is null ? string.Empty : JsonSerializer.Serialize(pause, RedisJsonContext.Default.PauseState);
        var result = await Db.ScriptEvaluateAsync(
            SetPauseInOrderScript, [ControlKey("pause"), ControlKey("decided")], [json, Micros(decidedAt)]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var json = state is null ? string.Empty : JsonSerializer.Serialize(state, RedisJsonContext.Default.SwitchState);
        var result = await Db.ScriptEvaluateAsync(
            SetSwitchInOrderScript, [ControlKey("switches"), ControlKey("decided")], [name, json, Micros(decidedAt)]).ConfigureAwait(false);
        return (int)result == 1;
    }

    // KEYS[1] = switches, KEYS[2] = decided; ARGV[1] = name, ARGV[2] = condition, ARGV[3] = instance ('' any),
    // ARGV[4] = decided at. Removes the switch only if that condition (and instance) still owns it, and
    // nothing was decided about it after the release (the condition claimed it again).
    private const string ReleaseSwitchScript = """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        if not current then
          return 0
        end
        local ok, state = pcall(cjson.decode, current)
        if not ok or type(state) ~= 'table' or state['condition'] ~= ARGV[2] or (ARGV[3] ~= '' and state['instance'] ~= ARGV[3]) then
          return 0
        end
        -- Refused when the condition claimed it again, or its owner renewed the claim, after the release was decided.
        local field = 'switch:' .. ARGV[1]
        local latest = tonumber(redis.call('HGET', KEYS[2], field) or '-1')
        local renewed = tonumber(redis.call('HGET', KEYS[2], 'renewed:' .. ARGV[1]) or '-1')
        local at = tonumber(ARGV[4])
        if at < latest or renewed > at then
          return 0
        end
        if at > latest then
          redis.call('HSET', KEYS[2], field, ARGV[4])
        end
        redis.call('HDEL', KEYS[2], 'claim:' .. ARGV[1], 'renewed:' .. ARGV[1])
        redis.call('HDEL', KEYS[1], ARGV[1])
        return 1
        """;

    public async ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Condition);
        var renewal = state.RenewedAt is { } renewed && renewed != state.At;
        var result = await Db.ScriptEvaluateAsync(
            ClaimSwitchScript,
            [ControlKey("switches"), ControlKey("decided")],
            [
                state.Name, JsonSerializer.Serialize(state, RedisJsonContext.Default.SwitchState), state.Condition, Micros(state.At),
                renewal ? "1" : "0", state.Instance ?? string.Empty,
                renewal && state.RenewedAt > DateTimeOffset.UnixEpoch ? Micros(state.RenewedAt.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            ]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        var result = await Db.ScriptEvaluateAsync(
            ReleaseSwitchScript, [ControlKey("switches"), ControlKey("decided")], [name, condition, instance ?? string.Empty, Micros(decidedAt)]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await Db.HashSetAsync(ControlKey("approvals"), approval.Request.Id, JsonSerializer.Serialize(approval, RedisJsonContext.Default.ApprovalRecord))
            .ConfigureAwait(false);
    }

    // KEYS[1] = approvals, KEYS[2] = answers; ARGV[1] = id, ARGV[2] = answer. Answers only an existing, unanswered request.
    private const string ResolveApprovalScript = """
        if redis.call('HEXISTS', KEYS[1], ARGV[1]) == 1 and redis.call('HSETNX', KEYS[2], ARGV[1], ARGV[2]) == 1 then
          return 1
        end
        return 0
        """;

    public async ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var answer = $"{status}\n{resolvedBy}";
        var result = await Db.ScriptEvaluateAsync(ResolveApprovalScript, [ControlKey("approvals"), ControlKey("answers")], [id, answer]).ConfigureAwait(false);
        return (int)result == 1;
    }

    // KEYS[1] = approvals, KEYS[2] = answers; ARGV = ids. Removes requests and their answers together, so a request is
    // never left without its answer (it would be listed as pending again).
    private const string RemoveApprovalsScript = """
        redis.call('HDEL', KEYS[1], unpack(ARGV))
        redis.call('HDEL', KEYS[2], unpack(ARGV))
        return 1
        """;

    public async ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await RemoveApprovalsAsync([id]).ConfigureAwait(false);
    }

    private async Task RemoveApprovalsAsync(RedisValue[] ids)
    {
        // In chunks: Lua's unpack takes a few thousand values at most.
        foreach (var chunk in ids.Chunk(1000))
        {
            await Db.ScriptEvaluateAsync(RemoveApprovalsScript, [ControlKey("approvals"), ControlKey("answers")], chunk).ConfigureAwait(false);
        }
    }

    // One hash tag for all the control keys: on Redis Cluster they share a slot, so the approval script can use two of them.
    private RedisKey ControlKey(string name) => $"{_options.KeyPrefix}{{control}}:{name}";

    private static T? Read<T>(RedisValue value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return value.HasValue ? JsonSerializer.Deserialize((string)value!, typeInfo) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private RedisKey PlanKey(string key) => $"{_options.KeyPrefix}plan:{key}";

    private static PlanState ToState(RedisValue[] values)
    {
        if (values.Length < 4 || values.All(v => v.IsNull))
        {
            return PlanState.Empty;
        }

        var ended = values[0].HasValue && long.TryParse((string?)values[0], out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : (DateTimeOffset?)null;
        var failures = values[1].HasValue && int.TryParse((string?)values[1], out var f) ? f : 0;
        var held = values[2].HasValue && (string?)values[2] == "1";
        var by = values[3].HasValue && ((string?)values[3])?.Length > 0 ? (string?)values[3] : null;
        return new PlanState { LastRunEndedAt = ended, ConsecutiveFailures = failures, Held = held, LastRunBy = by };
    }
}

/// <summary>Serialization metadata generated at compile time: no reflection, trimming and Native AOT safe.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HealingRunRecord))]
[JsonSerializable(typeof(ConditionEventRecord))]
[JsonSerializable(typeof(PauseState))]
[JsonSerializable(typeof(SwitchState))]
[JsonSerializable(typeof(ApprovalRecord))]
internal sealed partial class RedisJsonContext : JsonSerializerContext;

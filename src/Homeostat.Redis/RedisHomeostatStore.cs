using System.Text.Json;
using System.Text.Json.Serialization;
using Homeostat.Healing;
using Homeostat.Persistence;
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
    // held (-1 keep, 0, 1), precondition on 'by' ('' none), retention (ms). Returns ended, failures, held, by.
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
        redis.call('PEXPIRE', KEYS[1], ARGV[6])
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

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisStoreOptions _options;
    private readonly string _budgetKey;
    private readonly TimeProvider _timeProvider;

    public RedisHomeostatStore(IConnectionMultiplexer redis, IOptions<RedisStoreOptions> options, IOptions<HomeostatOptions> homeostat, TimeProvider? timeProvider = null)
    {
        _redis = redis;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _budgetKey = _options.ShareBudget
            ? $"{_options.KeyPrefix}budget"
            : $"{_options.KeyPrefix}budget:{homeostat.Value.InstanceId}";
    }

    private IDatabase Db => _redis.GetDatabase();

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
            // Unreadable (for example written by a newer version during a rolling upgrade): left for its writer.
            if (Read(entry.Value, RedisJsonContext.Default.ApprovalRecord) is not { } record)
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
            await Db.HashDeleteAsync(ControlKey("approvals"), [.. abandoned]).ConfigureAwait(false);
            await Db.HashDeleteAsync(ControlKey("answers"), [.. abandoned]).ConfigureAwait(false);
        }

        var pauseValue = await pause.ConfigureAwait(false);
        return new ControlState
        {
            Pause = pauseValue.HasValue ? Read(pauseValue, RedisJsonContext.Default.PauseState) : null,
            Switches = (await switches.ConfigureAwait(false))
                .Select(e => Read(e.Value, RedisJsonContext.Default.SwitchState))
                .OfType<SwitchState>()
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToArray(),
            Approvals = records.OrderBy(r => r.Request.RequestedAt).ToArray(),
        };
    }

    public async ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default)
    {
        if (pause is null)
        {
            await Db.KeyDeleteAsync(ControlKey("pause")).ConfigureAwait(false);
        }
        else
        {
            await Db.StringSetAsync(ControlKey("pause"), JsonSerializer.Serialize(pause, RedisJsonContext.Default.PauseState)).ConfigureAwait(false);
        }
    }

    public async ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (state is null)
        {
            await Db.HashDeleteAsync(ControlKey("switches"), name).ConfigureAwait(false);
        }
        else
        {
            await Db.HashSetAsync(ControlKey("switches"), name, JsonSerializer.Serialize(state, RedisJsonContext.Default.SwitchState)).ConfigureAwait(false);
        }
    }

    // KEYS[1] = switches; ARGV[1] = name, ARGV[2] = state (JSON), ARGV[3] = condition. Sets the switch only if it is off
    // or owned by the same condition.
    private const string ClaimSwitchScript = """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        if current then
          local ok, state = pcall(cjson.decode, current)
          if not ok or type(state) ~= 'table' or state['condition'] ~= ARGV[3] then
            return 0
          end
        end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        return 1
        """;

    // KEYS[1] = switches; ARGV[1] = name, ARGV[2] = condition, ARGV[3] = instance ('' any). Removes the switch only if
    // that condition (and instance) still owns it.
    private const string ReleaseSwitchScript = """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        if not current then
          return 0
        end
        local ok, state = pcall(cjson.decode, current)
        if not ok or type(state) ~= 'table' or state['condition'] ~= ARGV[2] or (ARGV[3] ~= '' and state['instance'] ~= ARGV[3]) then
          return 0
        end
        redis.call('HDEL', KEYS[1], ARGV[1])
        return 1
        """;

    public async ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Condition);
        var result = await Db.ScriptEvaluateAsync(
            ClaimSwitchScript,
            [ControlKey("switches")],
            [state.Name, JsonSerializer.Serialize(state, RedisJsonContext.Default.SwitchState), state.Condition]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        var result = await Db.ScriptEvaluateAsync(ReleaseSwitchScript, [ControlKey("switches")], [name, condition, instance ?? string.Empty]).ConfigureAwait(false);
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

    public async ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await Db.HashDeleteAsync(ControlKey("approvals"), id).ConfigureAwait(false);
        await Db.HashDeleteAsync(ControlKey("answers"), id).ConfigureAwait(false);
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

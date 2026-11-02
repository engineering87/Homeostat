using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
    private readonly RedisHomeostatOptions _options;
    private readonly string _budgetKey;

    public RedisHomeostatStore(IConnectionMultiplexer redis, IOptions<RedisHomeostatOptions> options, IOptions<HomeostatOptions> homeostat)
    {
        _redis = redis;
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
        var push = batch.ListLeftPushAsync(key, JsonSerializer.Serialize(run, Json));
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
                if (JsonSerializer.Deserialize<HealingRunRecord>((string)value!, Json) is { } run)
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
        var push = batch.ListLeftPushAsync(key, JsonSerializer.Serialize(conditionEvent, Json));
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
                if (JsonSerializer.Deserialize<ConditionEventRecord>((string)value!, Json) is { } conditionEvent)
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

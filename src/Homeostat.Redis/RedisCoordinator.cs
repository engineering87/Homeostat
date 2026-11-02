using Homeostat.Coordination;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat.Redis;

/// <summary>
/// <see cref="IHomeostatCoordinator"/> on Redis: a lease is a key set with <c>NX</c> and an expiry, owned through a
/// random token, renewed in the background at a third of its duration and released only by its owner.
/// </summary>
internal sealed class RedisCoordinator : IHomeostatCoordinator
{
    // KEYS[1] = lease; ARGV[1] = token; ARGV[2] = duration (ms).
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    // KEYS[1] = lease; ARGV[1] = token.
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisHomeostatOptions _options;
    private readonly ILogger<RedisCoordinator> _logger;

    public RedisCoordinator(IConnectionMultiplexer redis, IOptions<RedisHomeostatOptions> options, ILogger<RedisCoordinator> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<IHomeostatLease?> TryAcquireAsync(string resource, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var key = (RedisKey)$"{_options.KeyPrefix}lease:{resource}";
        var token = Guid.NewGuid().ToString("N");

        var acquired = await _redis.GetDatabase().StringSetAsync(key, token, duration, When.NotExists).ConfigureAwait(false);
        return acquired ? new Lease(this, key, token, duration) : null;
    }

    private sealed class Lease : IHomeostatLease
    {
        private readonly RedisCoordinator _owner;
        private readonly RedisKey _key;
        private readonly string _token;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lost = new();
        private readonly Task _renewal;

        public Lease(RedisCoordinator owner, RedisKey key, string token, TimeSpan duration)
        {
            _owner = owner;
            _key = key;
            _token = token;
            _renewal = RenewAsync(duration);
        }

        public CancellationToken Lost => _lost.Token;

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            try
            {
                await _owner._redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [_key], [_token]).ConfigureAwait(false);
            }
            finally
            {
                _stop.Dispose();
                _lost.Dispose();
            }
        }

        private async Task RenewAsync(TimeSpan duration)
        {
            var interval = duration / 3;
            var lastRenewal = System.Diagnostics.Stopwatch.StartNew();
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                try
                {
                    var renewed = await _owner._redis.GetDatabase()
                        .ScriptEvaluateAsync(RenewScript, [_key], [_token, (long)duration.TotalMilliseconds]).ConfigureAwait(false);
                    if ((int)renewed == 0)
                    {
                        _owner._logger.LogWarning("The Homeostat lease {Lease} was lost; another instance may take over.", (string?)_key);
                        _lost.Cancel();
                        return;
                    }

                    lastRenewal.Restart();
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    _owner._logger.LogWarning(ex, "Could not renew the Homeostat lease {Lease}; retrying.", (string?)_key);
                }

                // Without a successful renewal for a whole lease duration, the key may have expired: assume it is lost.
                if (lastRenewal.Elapsed >= duration)
                {
                    _owner._logger.LogWarning("The Homeostat lease {Lease} could not be renewed in time; another instance may take over.", (string?)_key);
                    _lost.Cancel();
                    return;
                }
            }
        }
    }
}

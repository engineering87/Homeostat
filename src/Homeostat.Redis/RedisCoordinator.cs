using System.Diagnostics;
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
    private readonly RedisStoreOptions _options;
    private readonly ILogger<RedisCoordinator> _logger;

    public RedisCoordinator(IConnectionMultiplexer redis, IOptions<RedisStoreOptions> options, ILogger<RedisCoordinator> logger)
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

        // The expiry runs from when Redis applies the command, which is after this moment: measuring from here is safe.
        var requestedAt = Stopwatch.GetTimestamp();
        var acquired = await _redis.GetDatabase().StringSetAsync(key, token, duration, When.NotExists).ConfigureAwait(false);
        return acquired ? new Lease(this, key, token, duration, requestedAt) : null;
    }

    private sealed class Lease : IHomeostatLease
    {
        private readonly RedisCoordinator _owner;
        private readonly RedisKey _key;
        private readonly string _token;
        private readonly TimeSpan _duration;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lost = new();
        private readonly Task _renewal;
        private readonly Task _watchdog;

        // Stopwatch timestamp until which the key certainly exists: the send time of the last successful renewal (or of
        // the acquisition) plus the duration.
        private long _validUntil;
        private int _lostSignaled;

        public Lease(RedisCoordinator owner, RedisKey key, string token, TimeSpan duration, long requestedAt)
        {
            _owner = owner;
            _key = key;
            _token = token;
            _duration = duration;
            _validUntil = requestedAt + ToTimestamp(duration);
            _renewal = RenewAsync();
            _watchdog = WatchAsync();
        }

        public CancellationToken Lost => _lost.Token;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { _renewal, _watchdog })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Stopping: a renewal that failed or was canceled must not prevent the release below.
                }
            }

            try
            {
                await _owner._redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [_key], [_token]).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Not fatal: the key expires by itself at the end of its duration.
                _owner._logger.LogWarning(ex, "Could not release the Homeostat lease {Lease}; it expires by itself.", (string?)_key);
            }
            finally
            {
                _stop.Dispose();
                _lost.Dispose();
            }
        }

        private async Task RenewAsync()
        {
            var interval = _duration / 3;
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                var sentAt = Stopwatch.GetTimestamp();
                try
                {
                    var renewed = await _owner._redis.GetDatabase()
                        .ScriptEvaluateAsync(RenewScript, [_key], [_token, (long)_duration.TotalMilliseconds]).ConfigureAwait(false);
                    if ((int)renewed == 0)
                    {
                        MarkLost(expired: false);
                        return;
                    }

                    // Only forward: a late reply to an older renewal must not shorten the validity.
                    var validUntil = sentAt + ToTimestamp(_duration);
                    long current;
                    while ((current = Interlocked.Read(ref _validUntil)) < validUntil
                        && Interlocked.CompareExchange(ref _validUntil, validUntil, current) != current)
                    {
                        // Another update won the race: read the new value and try again.
                    }
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    _owner._logger.LogWarning(ex, "Could not renew the Homeostat lease {Lease}; retrying.", (string?)_key);
                }
            }
        }

        /// <summary>
        /// Declares the lease lost before the key can expire, independently of renewals that may be blocked on an
        /// unreachable server: a run must stop before another instance is able to take the lease.
        /// </summary>
        private async Task WatchAsync()
        {
            // Margin for the difference between the two clocks and for the run to notice before its next step.
            var margin = ToTimestamp(_duration / 6);
            while (!_stop.IsCancellationRequested)
            {
                var remaining = Interlocked.Read(ref _validUntil) - margin - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    MarkLost(expired: true);
                    return;
                }

                await Task.Delay(Stopwatch.GetElapsedTime(0, remaining), _stop.Token).ConfigureAwait(false);
            }
        }

        private void MarkLost(bool expired)
        {
            if (Interlocked.Exchange(ref _lostSignaled, 1) != 0)
            {
                return;
            }

            if (expired)
            {
                _owner._logger.LogWarning("The Homeostat lease {Lease} could not be renewed in time; another instance may take over.", (string?)_key);
            }
            else
            {
                _owner._logger.LogWarning("The Homeostat lease {Lease} was lost; another instance may take over.", (string?)_key);
            }

            _lost.Cancel();
        }

        private static long ToTimestamp(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
    }
}

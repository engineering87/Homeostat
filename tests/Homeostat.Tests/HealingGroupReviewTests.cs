using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

/// <summary>
/// Findings of the independent review of healing groups: the limit of 256 group pauses when this instance's view is
/// behind the store or was never read (G4), the pause shown in the status (G6), and what the file store keeps of group
/// budgets and group pauses (F7, S8).
/// </summary>
public class HealingGroupReviewTests
{
    private const string LimitMessage = "At most 256 healing groups can be paused at once";

    // ---------------------------------------------------------------- finding 1: the store refuses the 257th pause

    [Fact]
    public async Task A_group_pause_the_store_refuses_for_its_limit_from_a_stale_view_fails_and_is_not_deferred()
    {
        var store = new DownStore();
        var logs = new LogCollector();
        await using var a = await StartInstanceAsync("a", store, logs);
        await using var b = await StartInstanceAsync("b", store);
        for (var i = 0; i < ControlState.MaxGroupPauses - 1; i++)
        {
            await store.Inner.TrySetGroupPauseAsync($"group-{i:000}", new PauseState { At = a.Time.GetUtcNow() }, a.Time.GetUtcNow());
        }

        await RefreshAsync(a);
        await RefreshAsync(b);
        Assert.Equal(ControlState.MaxGroupPauses - 1, Shared(a).Current.GroupPauses.Count);

        // b pauses the 256th group; a still shows 255 (within its refresh interval) when a person pauses one more there.
        await b.Control.PauseGroupAsync("exports", by: "deploy");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => a.Control.PauseGroupAsync("reports", by: "on-call").AsTask());
        Assert.Contains(LimitMessage, error.Message);

        // Not applied here, not in the store, and not retried: even once room is made, a refresh does not write it.
        Assert.False(a.Control.IsGroupPaused("reports"));
        Assert.DoesNotContain(await GroupsInStoreAsync(store), g => g == "reports");
        a.Time.Advance(TimeSpan.FromSeconds(1));
        b.Time.Advance(TimeSpan.FromSeconds(1));
        await b.Control.ResumeGroupAsync("group-000");
        await RefreshAsync(a);
        Assert.DoesNotContain(await GroupsInStoreAsync(store), g => g == "reports");
        Assert.False(a.Control.IsGroupPaused("reports"));
        Assert.DoesNotContain(logs.Messages(LogLevel.Warning), m => m.Contains("retried at every refresh", StringComparison.Ordinal));

        // With room, the same pause is taken.
        await a.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.Contains(await GroupsInStoreAsync(store), g => g == "reports");
    }

    [Fact]
    public async Task A_group_pause_deferred_during_an_outage_and_then_refused_for_the_limit_is_dropped_with_a_warning()
    {
        var store = new DownStore();
        var logs = new LogCollector();
        await using var a = await StartInstanceAsync("a", store, logs);
        for (var i = 0; i < ControlState.MaxGroupPauses - 1; i++)
        {
            await store.Inner.TrySetGroupPauseAsync($"group-{i:000}", new PauseState { At = a.Time.GetUtcNow() }, a.Time.GetUtcNow());
        }

        await RefreshAsync(a);

        // The store is down: the pause applies here at once and is deferred (S1).
        store.Down = true;
        await a.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.True(a.Control.IsGroupPaused("reports"));

        // Meanwhile another instance took the last room; the store recovers and refuses the deferred pause.
        await store.Inner.TrySetGroupPauseAsync("exports", new PauseState { At = a.Time.GetUtcNow() }, a.Time.GetUtcNow());
        store.Down = false;
        await RefreshAsync(a);

        Assert.False(a.Control.IsGroupPaused("reports"));
        Assert.True(a.Control.IsGroupPaused("exports"));
        Assert.Contains(logs.Messages(LogLevel.Warning), m => m.Contains("group-pause:reports", StringComparison.Ordinal) && m.Contains(LimitMessage, StringComparison.Ordinal));

        // Dropped: not written once room is made.
        await store.Inner.TrySetGroupPauseAsync("group-000", null, a.Time.GetUtcNow().AddSeconds(1));
        await RefreshAsync(a);
        Assert.DoesNotContain(await GroupsInStoreAsync(store), g => g == "reports");
    }

    [Fact]
    public async Task A_store_that_fails_group_pause_writes_with_InvalidOperationException_below_the_limit_defers_the_pause()
    {
        // Some stores and clients throw InvalidOperationException in an outage: only a read showing the limit makes it
        // a refusal. Here the reads work and show room: the pause is deferred and written once the store takes writes.
        var store = new DownStore();
        await using var a = await StartInstanceAsync("a", store);
        await RefreshAsync(a);

        store.WritesDown = true;
        await a.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.True(a.Control.IsGroupPaused("reports"));
        Assert.Empty((await store.Inner.GetControlStateAsync()).GroupPauses);

        store.WritesDown = false;
        await RefreshAsync(a);
        Assert.Equal("on-call", Assert.Single((await store.Inner.GetControlStateAsync()).GroupPauses).By);
    }

    [Fact]
    public async Task The_group_pause_endpoint_answers_409_when_the_store_refuses_the_pause_for_its_limit()
    {
        var store = new InMemoryHomeostatStore();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h.UseStore(_ => store).Configure(o => o.ControlRefreshInterval = TimeSpan.FromHours(1)));
        await using var app = builder.Build();
        app.MapHomeostatControl(allowAnonymous: true);
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Requested-With", "tests");

        // Paused elsewhere after this instance read the state: its view shows none of them.
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < ControlState.MaxGroupPauses; i++)
        {
            await store.TrySetGroupPauseAsync($"group-{i:000}", new PauseState { At = now }, now);
        }

        using var response = await client.PostAsync("/homeostat/groups/reports/pause", null);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(LimitMessage, await response.Content.ReadAsStringAsync());
        Assert.False(app.Services.GetRequiredService<IHomeostatControl>().IsGroupPaused("reports"));
        Assert.DoesNotContain((await store.GetControlStateAsync()).GroupPauses, p => p.Group == "reports");
    }

    // ---------------------------------------------------------------- finding 2: the limit before the first read

    [Fact]
    public async Task Before_the_state_is_first_read_the_pending_group_pauses_count_toward_the_limit()
    {
        var store = new DownStore { Down = true };
        await using var host = await StartInstanceAsync("a", store);
        Assert.False(Shared(host).IsKnown);

        for (var i = 0; i < ControlState.MaxGroupPauses; i++)
        {
            await host.Control.PauseGroupAsync($"group-{i:000}", by: "on-call");
        }

        // One more group is refused at once, as with a known state; a group already pending may be paused again.
        Assert.Throws<InvalidOperationException>(() => { _ = host.Control.PauseGroupAsync("one-too-many"); });
        await host.Control.PauseGroupAsync("group-000", by: "again");
        Assert.Equal(ControlState.MaxGroupPauses, Shared(host).Known.GroupPauses.Count);

        // The store recovers: the pending pauses are written, the refused one is not.
        store.Down = false;
        await RefreshAsync(host);
        var stored = (await store.Inner.GetControlStateAsync()).GroupPauses;
        Assert.Equal(ControlState.MaxGroupPauses, stored.Count);
        Assert.Equal("again", stored.Single(p => p.Group == "group-000").By);
        Assert.DoesNotContain(stored, p => p.Group == "one-too-many");
    }

    // ---------------------------------------------------------------- finding 3: the pause shown in the status

    [Fact]
    public void The_status_shows_who_paused_a_group_only_while_the_pause_applies_even_when_a_window_pauses_the_group()
    {
        var now = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
        var ended = GroupPauseState.From("reports", new PauseState { At = now.AddHours(-2), Until = now.AddHours(-1), By = "deploy" });
        var group = new HealingGroupStatus
        {
            Name = "reports",
            Conditions = [],
            IsPaused = true,   // a maintenance window of the group is open
            PauseReason = "maintenance window 'reports-nightly'",
            Pause = ended,
        };

        var info = AspNetCore.GroupStatusInfo.From(group, budget: null, now);
        Assert.True(info.Paused);
        Assert.Null(info.PausedBy);
        Assert.Null(info.PausedUntil);

        // While the pause applies (timed or until resumed), it is shown.
        var timed = AspNetCore.GroupStatusInfo.From(group with { Pause = PauseOf(now.AddMinutes(5)) }, budget: null, now);
        Assert.Equal("deploy", timed.PausedBy);
        Assert.Equal(now.AddMinutes(5), timed.PausedUntil);
        Assert.Equal("deploy", AspNetCore.GroupStatusInfo.From(group with { Pause = PauseOf(null) }, budget: null, now).PausedBy);

        // At its end exactly, a timed pause no longer applies (as IsGroupPaused).
        Assert.Null(AspNetCore.GroupStatusInfo.From(group with { Pause = PauseOf(now) }, budget: null, now).PausedBy);

        GroupPauseState PauseOf(DateTimeOffset? until) => GroupPauseState.From("reports", new PauseState { At = now.AddHours(-2), Until = until, By = "deploy" });
    }

    // ---------------------------------------------------------------- finding 4: empty group budgets are not kept

    [Fact]
    public async Task The_file_store_forgets_the_budget_of_a_group_whose_units_left_their_window_even_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-group-budget-{Guid.NewGuid():N}.json");
        var t0 = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
        try
        {
            using (var store = new FileHomeostatStore(path))
            {
                Assert.True(await store.TryConsumeGroupBudgetAsync("old-name", t0, limit: 5, TimeSpan.FromHours(1)));
                Assert.True(await store.TryConsumeGroupBudgetAsync("reports", t0, limit: 5, TimeSpan.FromHours(4)));
            }

            Assert.Contains("old-name", GroupBudgetsInFile(path));

            // After a restart, the group that was renamed is never asked about again: another group's call removes it
            // once its units left their window, and the next save leaves it out. A group within its window is kept.
            using (var store = new FileHomeostatStore(path))
            {
                Assert.True(await store.TryConsumeGroupBudgetAsync("ingest", t0.AddHours(2), limit: 5, TimeSpan.FromHours(1)));
                Assert.Equal(1, await store.GetGroupBudgetUsedAsync("reports", t0.AddHours(2), TimeSpan.FromHours(4)));
            }

            Assert.Equal(["ingest", "reports"], GroupBudgetsInFile(path));

            // A read of a budget whose units all left their window removes it too.
            using (var store = new FileHomeostatStore(path))
            {
                Assert.Equal(0, await store.GetGroupBudgetUsedAsync("ingest", t0.AddHours(3), TimeSpan.FromHours(1)));
                Assert.True(await store.TryConsumeBudgetAsync(t0.AddHours(3), limit: 5, TimeSpan.FromHours(1)));   // a change saves the file
            }

            Assert.Equal(["reports"], GroupBudgetsInFile(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    // ---------------------------------------------------------------- finding 5: the file store keeps every group pause

    [Fact]
    public async Task The_file_store_keeps_every_group_pause_of_its_file_and_the_latest_of_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-group-pauses-{Guid.NewGuid():N}.json");
        var t0 = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
        try
        {
            using (var store = new FileHomeostatStore(path))
            {
                await store.TrySetGroupPauseAsync("reports", new PauseState { At = t0, By = "first" }, t0);
            }

            // A hand edit: 300 pauses, and the same group twice, the later decision written first.
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var pausesKey = document.Single(p => p.Value is JsonArray && p.Key.Equals("groupPauses", StringComparison.OrdinalIgnoreCase)).Key;
            var pauses = document[pausesKey]!.AsArray();
            var template = pauses[0]!.AsObject();
            var groupKey = template.Single(p => p.Key.Equals("group", StringComparison.OrdinalIgnoreCase)).Key;
            var byKey = template.Single(p => p.Key.Equals("by", StringComparison.OrdinalIgnoreCase)).Key;
            var atKey = template.Single(p => p.Key.Equals("at", StringComparison.OrdinalIgnoreCase)).Key;
            var later = template.DeepClone().AsObject();
            later[byKey] = "later";
            later[atKey] = t0.AddMinutes(5).ToString("O");
            pauses.Insert(0, later);
            for (var i = 0; i < 299; i++)
            {
                var other = template.DeepClone().AsObject();
                other[groupKey] = $"group-{i:000}";
                pauses.Add(other);
            }

            File.WriteAllText(path, document.ToJsonString());

            using (var store = new FileHomeostatStore(path))
            {
                var stored = (await store.GetControlStateAsync()).GroupPauses;
                Assert.Equal(300, stored.Count);
                Assert.Equal("later", stored.Single(p => p.Group == "reports").By);

                // The limit refuses new pauses only; replacing and resuming still work.
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.TrySetGroupPauseAsync("new", new PauseState { At = t0.AddHours(1) }, t0.AddHours(1)).AsTask());
                Assert.True(await store.TrySetGroupPauseAsync("group-000", null, t0.AddHours(1)));
                Assert.Equal(299, (await store.GetControlStateAsync()).GroupPauses.Count);
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Engine.SharedControl Shared(HomeostatTestHost host) => host.Services.GetRequiredService<Engine.SharedControl>();

    private static Task<HomeostatTestHost> StartInstanceAsync(string instance, IHomeostatStore store, LogCollector? logs = null)
        => HomeostatTestHost.StartAsync(
            h => h.UseStore(_ => store).Configure(o =>
            {
                o.InstanceId = instance;
                o.ControlRefreshInterval = TimeSpan.FromSeconds(5);
            }),
            configureServices: logs is null ? null : services => services.AddLogging(l => l.AddProvider(logs)),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

    // Twice: a refresh may join a read that started before the change being checked (decision P8).
    private static async Task RefreshAsync(HomeostatTestHost host)
    {
        await Shared(host).TryRefreshAsync(CancellationToken.None);
        await Shared(host).TryRefreshAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<string>> GroupsInStoreAsync(DownStore store)
        => (await store.Inner.GetControlStateAsync()).GroupPauses.Select(p => p.Group).ToArray();

    private static string[] GroupBudgetsInFile(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var budgets = document.SingleOrDefault(p => p.Key.Equals("groupBudgets", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        return budgets is null ? [] : [.. budgets.Select(b => b.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary>Collects log messages.</summary>
    private sealed class LogCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Messages(LogLevel level) => _entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCollector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner._entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>An in-memory store that can be down, or fail only its writes of group pauses, with InvalidOperationException as some clients do.</summary>
    private sealed class DownStore : IHomeostatStore
    {
        public InMemoryHomeostatStore Inner { get; } = new();

        public volatile bool Down;

        public volatile bool WritesDown;

        private void Check()
        {
            if (Down)
            {
                throw new InvalidOperationException("store down");
            }
        }

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            return WritesDown ? throw new InvalidOperationException("store down") : Inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);
        }

        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken); }

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken); }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) { Check(); return Inner.GetPlanStateAsync(key, cancellationToken); }

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) { Check(); return Inner.UpdatePlanStateAsync(key, update, cancellationToken); }

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken); }

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.GetBudgetUsedAsync(now, window, cancellationToken); }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) { Check(); return Inner.AppendRunAsync(run, maxRuns, cancellationToken); }

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) { Check(); return Inner.GetRecentRunsAsync(maxRuns, cancellationToken); }

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) { Check(); return Inner.GetControlStateAsync(cancellationToken); }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TrySetPauseAsync(pause, decidedAt, cancellationToken); }

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Check(); return Inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Check(); return Inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Check(); return Inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Check(); return Inner.RemoveApprovalAsync(id, cancellationToken); }
    }
}

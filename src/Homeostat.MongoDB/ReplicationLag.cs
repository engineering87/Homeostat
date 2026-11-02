namespace Homeostat.MongoDB;

/// <summary>A member of the replica set, as reported by <c>replSetGetStatus</c>.</summary>
internal readonly record struct ReplicaMember(string Name, string State, DateTime? OptimeUtc);

internal static class ReplicationLag
{
    private static readonly HashSet<string> Data = new(StringComparer.Ordinal) { "PRIMARY", "SECONDARY", "RECOVERING", "STARTUP2" };
    private static readonly HashSet<string> Followers = new(StringComparer.Ordinal) { "SECONDARY", "RECOVERING", "STARTUP2" };

    /// <summary>
    /// Seconds between the most recent operation of any data member and the furthest follower: secondaries, and
    /// members that fell behind (<c>RECOVERING</c>) or are syncing (<c>STARTUP2</c>), which are when the lag matters
    /// most. Measured against the freshest member, it works during an election too. 0 for a replica set with a single
    /// data member. Members listed in <paramref name="excluded"/> (delayed or hidden members) are left out.
    /// </summary>
    public static double Seconds(IEnumerable<ReplicaMember> members, ICollection<string>? excluded = null)
    {
        var data = members
            .Where(m => m.OptimeUtc is not null && Data.Contains(m.State) && (excluded is null || !excluded.Contains(m.Name)))
            .ToList();
        var followers = data.Where(m => Followers.Contains(m.State)).ToList();
        if (data.Count < 2 || followers.Count == 0)
        {
            return 0;
        }

        var freshest = data.Max(m => m.OptimeUtc!.Value);
        var slowest = followers.Min(m => m.OptimeUtc!.Value);
        return Math.Max(0, (freshest - slowest).TotalSeconds);
    }
}

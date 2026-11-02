namespace Homeostat.AspNetCore;

/// <summary>Settings of the Slack interactivity endpoint mapped with <c>MapHomeostatSlackInteractions</c>.</summary>
public sealed class SlackInteractionOptions
{
    /// <summary>Signing secret of the Slack app, used to verify that requests come from Slack. Required.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>
    /// Slack user IDs (for example <c>U024BE7LH</c>) allowed to approve or reject steps. Required unless
    /// <see cref="AllowAnyChannelMember"/> is <c>true</c>.
    /// </summary>
    public ISet<string> AllowedUserIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Lets anyone who can see the message answer approval requests. Defaults to <c>false</c>: approving a step changes
    /// what the application does to itself, so the people allowed to do it are listed in <see cref="AllowedUserIds"/>.
    /// </summary>
    public bool AllowAnyChannelMember { get; set; }

    /// <summary>Oldest accepted request, to stop replayed requests. Defaults to 5 minutes, as Slack recommends.</summary>
    public TimeSpan MaxRequestAge { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningSecret))
        {
            throw new ArgumentException("SlackInteractionOptions.SigningSecret is required.");
        }

        if (AllowedUserIds.Count == 0 && !AllowAnyChannelMember)
        {
            throw new ArgumentException(
                "List the Slack users allowed to approve in AllowedUserIds, or set AllowAnyChannelMember to true.");
        }

        if (MaxRequestAge <= TimeSpan.Zero)
        {
            throw new ArgumentException("MaxRequestAge must be positive.");
        }
    }
}

namespace Homeostat.AspNetCore;

/// <summary>Settings of the Microsoft Teams command endpoint mapped with <c>MapHomeostatTeamsCommands</c>.</summary>
public sealed class TeamsCommandOptions
{
    /// <summary>
    /// Security token that Teams shows when the outgoing webhook is created (base64), used to verify that requests come
    /// from Teams. Required.
    /// </summary>
    public string? SecurityToken { get; set; }

    /// <summary>
    /// Microsoft Entra object IDs of the users allowed to approve or reject steps (the <c>aadObjectId</c> of the
    /// sender). Required unless <see cref="AllowAnyTeamMember"/> is <c>true</c>.
    /// </summary>
    public ISet<string> AllowedUserIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lets any member of the team approve or reject steps. Defaults to <c>false</c>: an outgoing webhook can be
    /// mentioned by everyone in the team, and approving a step changes what the application does to itself.
    /// </summary>
    public bool AllowAnyTeamMember { get; set; }

    /// <summary>Oldest accepted message, against replayed requests. Defaults to 5 minutes.</summary>
    public TimeSpan MaxRequestAge { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(SecurityToken))
        {
            throw new ArgumentException("TeamsCommandOptions.SecurityToken is required.");
        }

        try
        {
            if (Convert.FromBase64String(SecurityToken).Length < 16)
            {
                throw new ArgumentException("TeamsCommandOptions.SecurityToken is too short: use the token Teams shows for the outgoing webhook.");
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException("TeamsCommandOptions.SecurityToken must be the base64 token Teams shows for the outgoing webhook.");
        }

        if (AllowedUserIds.Count == 0 && !AllowAnyTeamMember)
        {
            throw new ArgumentException(
                "List the Microsoft Entra object IDs of the users allowed to approve in AllowedUserIds, or set AllowAnyTeamMember to true.");
        }

        if (MaxRequestAge <= TimeSpan.Zero)
        {
            throw new ArgumentException("MaxRequestAge must be positive.");
        }
    }
}

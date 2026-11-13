using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homeostat.AspNetCore;
using Homeostat.Healing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Homeostat;

/// <summary>Maps the endpoint of a Microsoft Teams outgoing webhook that answers approval requests from a channel.</summary>
public static partial class HomeostatTeamsEndpointExtensions
{
    // Teams waits 5 seconds for the answer to an outgoing webhook: a store that does not answer gets a reply in time.
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(4);

    private const int MaxBodySize = 64 * 1024;

    /// <summary>
    /// Maps <c>POST {pattern}</c>, the callback URL of a Teams <b>outgoing webhook</b> (no bot registration or SDK
    /// needed). In the channel, people mention the webhook: <c>@Homeostat pending</c> lists the steps waiting for
    /// approval, <c>@Homeostat approve &lt;id&gt;</c> and <c>@Homeostat reject &lt;id&gt;</c> answer one. The endpoint
    /// verifies the HMAC signature and the message age, checks that the sender may approve, records the sender as the
    /// approver and replies in the thread.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configure">Sets the security token and the users allowed to approve.</param>
    /// <param name="pattern">Route of the endpoint.</param>
    /// <exception cref="ArgumentException">The security token or the allowed users are missing.</exception>
    /// <example>
    /// <code>
    /// app.MapHomeostatTeamsCommands(o =>
    /// {
    ///     o.SecurityToken = builder.Configuration["Teams:OutgoingWebhookToken"];
    ///     o.AllowedUserIds.Add("6f1c2a77-5b8e-4d55-9a8e-0c2f1e0b9d41");
    /// });
    /// </code>
    /// </example>
    public static RouteHandlerBuilder MapHomeostatTeamsCommands(
        this IEndpointRouteBuilder endpoints,
        Action<TeamsCommandOptions> configure,
        string pattern = "/homeostat/teams/messages")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var options = new TeamsCommandOptions();
        configure(options);
        options.Validate();
        var key = Convert.FromBase64String(options.SecurityToken!);
        var seen = new SeenMessages();

        // Teams authenticates with its signature, not with the application's users.
        return endpoints.MapPost(pattern, (HttpContext http, IHomeostatApprovals approvals, TimeProvider time, ILoggerFactory loggers)
                => HandleAsync(http, approvals, time, loggers.CreateLogger("Homeostat.Teams"), options, key, seen))
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IHomeostatApprovals approvals,
        TimeProvider time,
        ILogger logger,
        TeamsCommandOptions options,
        byte[] key,
        SeenMessages seen)
    {
        var body = await ReadBodyAsync(http.Request, http.RequestAborted);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!IsSignatureValid(http.Request.Headers.Authorization.ToString(), body, key))
        {
            return Results.Unauthorized();
        }

        TeamsMessage? message;
        try
        {
            message = TeamsMessage.Parse(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) // Not JSON, or a value of the wrong type.
        {
            return Results.BadRequest();
        }

        if (message is null)
        {
            return Results.BadRequest();
        }

        // Both are needed against replays: a signed message without them is refused, not trusted.
        if (message.Timestamp is not { } sent || message.Id is not { } id)
        {
            return Results.BadRequest();
        }

        var now = time.GetUtcNow();
        if ((now - sent).Duration() > options.MaxRequestAge)
        {
            logger.LogWarning("Teams message {MessageId} is {Age} old: ignored as a possible replay.", message.Id, now - sent);
            return Reply("This message is too old to act on. Send the command again.");
        }

        if (!seen.TryAdd(id, now, options.MaxRequestAge))
        {
            return Reply("This message was already handled.");
        }

        try
        {
            // Teams gives up after a few seconds: a store that does not answer gets a reply, not a hung request. The
            // command runs without the request's token, so a disconnect does not cut an answer short; only the wait ends.
            return await Task.Run(() => RunCommandAsync(message, approvals, options, logger, CancellationToken.None), CancellationToken.None).WaitAsync(StoreTimeout, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // Teams is gone. The message stays marked as handled: the command may still complete.
            return Results.Empty;
        }
        catch (Exception ex) when (StoreCall.IsOutcomeUnknown(ex))
        {
            // The command may still complete after this reply, so the message stays marked as handled: a replay of it
            // must not apply it a second time. The person checks with 'pending' and sends a new command if needed.
            logger.LogError(ex, "Teams command from {User}: the Homeostat store did not answer in time; it may still be carried out.", message.UserName);
            return Reply("Homeostat's store did not answer in time: the command may still be carried out. Send 'pending' to check before sending it again.");
        }
        catch (Exception ex)
        {
            // Nothing changed: the same message may be sent again.
            seen.Remove(id);
            logger.LogError(ex, "Teams command from {User} not carried out: the Homeostat store is unavailable.", message.UserName);
            return Reply("Homeostat cannot do this now (its store is unavailable); nothing was changed. Send the command again in a moment.");
        }
    }

    private static async Task<IResult> RunCommandAsync(
        TeamsMessage message, IHomeostatApprovals approvals, TeamsCommandOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        var words = message.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        switch (verb)
        {
            case "pending" or "status" or "list":
                return Reply(Pending(await approvals.GetPendingAsync(cancellationToken)));

            case "approve" or "reject" when words.Length == 2:
                {
                    var approve = verb == "approve";
                    var approvalId = words[1];
                    if (!options.AllowAnyTeamMember && (message.UserId is null || !options.AllowedUserIds.Contains(message.UserId)))
                    {
                        logger.LogWarning("Teams user {User} ({UserId}) is not allowed to answer approval {ApprovalId}.", message.UserName, message.UserId, approvalId);
                        return Reply($"{message.UserName} is not allowed to answer Homeostat approval requests.");
                    }

                    var by = $"teams:{message.UserName} ({message.UserId ?? "unknown"})";
                    var answered = approve
                        ? await approvals.ApproveAsync(approvalId, by, cancellationToken)
                        : await approvals.RejectAsync(approvalId, by, cancellationToken);
                    logger.LogInformation("Teams user {User} {Decision} approval {ApprovalId}: {Result}.",
                        message.UserName, approve ? "approved" : "rejected", approvalId, answered ? "done" : "not pending");
                    return Reply(answered
                        ? $"{(approve ? "Approved" : "Rejected")} by {message.UserName}: {approvalId}."
                        : $"{approvalId} is not pending (answered, expired, withdrawn, or a wrong id). Send 'pending' to list the requests.");
                }

            default:
                return Reply("Commands: `pending` lists the steps waiting for approval; `approve ID` and `reject ID` answer one.");
        }
    }

    internal static string Pending(IReadOnlyList<PendingApproval> pending)
    {
        if (pending.Count == 0)
        {
            return "Nothing is waiting for approval.";
        }

        var text = new StringBuilder("Waiting for approval:");
        foreach (var p in pending)
        {
            // Teams renders replies as Markdown: names are wrapped in code spans, so they show as typed.
            text.Append(CultureInfo.InvariantCulture,
                $"\n\n`{Code(p.Id)}`: `{Code(p.ConditionName)}`{(p.Group is { } group ? $" in group `{Code(group)}`" : string.Empty)}, step `{Code(p.StepName)}`, expires {p.ExpiresAt:u}");
        }

        return text.ToString();
    }

    private static string Code(string value) => value.Replace('`', '\'');

    private static IResult Reply(string text)
        => Results.Text(new JsonObject { ["type"] = "message", ["text"] = text }.ToJsonString(), "application/json", Encoding.UTF8);

    /// <summary>The body, or <c>null</c> when it is larger than <see cref="MaxBodySize"/> (declared or actual).</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodySize)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodySize)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Teams signs the request body with HMAC-SHA256, keyed with the base64-decoded security token, and sends
    /// <c>Authorization: HMAC &lt;base64 signature&gt;</c>.
    /// </summary>
    internal static bool IsSignatureValid(string authorization, byte[] body, byte[] key)
    {
        if (!authorization.StartsWith("HMAC ", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(authorization["HMAC ".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), provided);
    }

    [GeneratedRegex("<at>.*?</at>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Mention();

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();

    /// <summary>A message that mentions the outgoing webhook.</summary>
    internal sealed record TeamsMessage(string? Id, string Command, string UserName, string? UserId, DateTimeOffset? Timestamp)
    {
        public static TeamsMessage? Parse(byte[] body)
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var textElement) || textElement.GetString() is not { } text
                || !root.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // "<at>Homeostat</at> approve 1f2e&nbsp;" -> "approve 1f2e"
            var command = Mention().Replace(text, " ");
            command = WebUtility.HtmlDecode(Tag().Replace(command, " "));
            command = Spaces().Replace(command, " ").Trim();

            var userId = from.TryGetProperty("aadObjectId", out var aad) && aad.GetString() is { Length: > 0 } objectId ? objectId : null;
            var name = from.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } display ? display : userId ?? "someone";
            var id = root.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 and <= 200 } messageId ? messageId : null;
            DateTimeOffset? timestamp = root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var at)
                ? at
                : null;
            return new TeamsMessage(id, command, name, userId, timestamp);
        }
    }

    /// <summary>Message ids seen within the accepted age, so a replayed request is not acted on twice.</summary>
    private sealed class SeenMessages
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

        public bool TryAdd(string id, DateTimeOffset now, TimeSpan maxAge)
        {
            if (_seen.Count > 1000)
            {
                foreach (var (key, at) in _seen)
                {
                    if (now - at > maxAge * 2)
                    {
                        _seen.TryRemove(key, out _);
                    }
                }
            }

            return _seen.TryAdd(id, now);
        }

        /// <summary>Forgets a message that could not be acted on, so sending it again works.</summary>
        public void Remove(string id) => _seen.TryRemove(id, out _);
    }
}

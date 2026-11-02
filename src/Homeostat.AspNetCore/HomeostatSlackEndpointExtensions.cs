using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Homeostat.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat;

/// <summary>Maps the endpoint that receives clicks on the Slack approval buttons.</summary>
public static class HomeostatSlackEndpointExtensions
{
    private const int MaxBodySize = 64 * 1024;
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Maps <c>POST {pattern}</c>, the interactivity request URL of the Slack app whose incoming webhook posts Homeostat
    /// approval requests (<c>AddSlackNotifications</c> with <c>ApprovalButtons = true</c>). It verifies the Slack
    /// signature and request age, checks that the user is allowed, answers the approval request (recording the Slack
    /// user as the approver), and replaces the buttons in the original message with the decision.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configure">Sets the signing secret and the users allowed to approve.</param>
    /// <param name="pattern">Route of the endpoint.</param>
    /// <exception cref="ArgumentException">The signing secret or the allowed users are missing.</exception>
    /// <example>
    /// <code>
    /// app.MapHomeostatSlackInteractions(o =>
    /// {
    ///     o.SigningSecret = builder.Configuration["Slack:SigningSecret"];
    ///     o.AllowedUserIds.Add("U024BE7LH");
    /// });
    /// </code>
    /// </example>
    public static RouteHandlerBuilder MapHomeostatSlackInteractions(
        this IEndpointRouteBuilder endpoints,
        Action<SlackInteractionOptions> configure,
        string pattern = "/homeostat/slack/interactions")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var options = new SlackInteractionOptions();
        configure(options);
        options.Validate();
        var secret = Encoding.UTF8.GetBytes(options.SigningSecret!);

        // Slack authenticates with its signature, not with the application's users.
        return endpoints.MapPost(pattern, (HttpContext http, IHomeostatApprovals approvals, TimeProvider time, ILoggerFactory loggers)
                => HandleAsync(http, approvals, time, loggers.CreateLogger("Homeostat.Slack"), options, secret))
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IHomeostatApprovals approvals,
        TimeProvider time,
        ILogger logger,
        SlackInteractionOptions options,
        byte[] secret)
    {
        if (http.Request.ContentLength > MaxBodySize)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(http.Request, http.RequestAborted);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!IsSignatureValid(http.Request, body, secret, time.GetUtcNow(), options.MaxRequestAge))
        {
            return Results.Unauthorized();
        }

        var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
        if (!form.TryGetValue("payload", out var payloadText) || payloadText.Count != 1)
        {
            return Results.BadRequest();
        }

        SlackAction? action;
        try
        {
            action = SlackAction.Parse(payloadText[0]!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) // Not JSON, or a value of the wrong type.
        {
            return Results.BadRequest();
        }

        if (action is null)
        {
            return Results.Ok(); // Another interaction (for example the dashboard link): nothing to do.
        }

        string reply;
        if (!options.AllowAnyChannelMember && !options.AllowedUserIds.Contains(action.UserId))
        {
            logger.LogWarning("Slack user {User} ({UserId}) is not allowed to answer approval {ApprovalId}.", action.UserName, action.UserId, action.ApprovalId);
            reply = $"<@{action.UserId}> is not allowed to answer Homeostat approval requests.";
            await RespondAsync(http, action.ResponseUrl, reply, replaceOriginal: false, logger);
            return Results.Ok();
        }

        // The user id is stable; the user name can be changed by the user.
        var by = $"slack:{action.UserName} ({action.UserId})";
        var answered = action.Approve ? approvals.Approve(action.ApprovalId, by) : approvals.Reject(action.ApprovalId, by);
        reply = answered
            ? $"{(action.Approve ? ":white_check_mark: Approved" : ":no_entry: Rejected")} by <@{action.UserId}>."
            : "This approval request is no longer pending (answered, expired or withdrawn).";
        logger.LogInformation("Slack user {User} {Decision} approval {ApprovalId}: {Result}.",
            action.UserName, action.Approve ? "approved" : "rejected", action.ApprovalId, answered ? "done" : "not pending");
        await RespondAsync(http, action.ResponseUrl, reply, replaceOriginal: answered, logger);
        return Results.Ok();
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
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

    /// <summary>Slack signs <c>v0:{timestamp}:{body}</c> with HMAC-SHA256 and the app's signing secret.</summary>
    internal static bool IsSignatureValid(HttpRequest request, byte[] body, byte[] secret, DateTimeOffset now, TimeSpan maxAge)
    {
        var timestampText = request.Headers["X-Slack-Request-Timestamp"].ToString();
        var signature = request.Headers["X-Slack-Signature"].ToString();
        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !signature.StartsWith("v0=", StringComparison.Ordinal))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(timestamp, 0, 253402300799));
        if (age.Duration() > maxAge)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"v0:{timestampText}:");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        var expected = Encoding.ASCII.GetBytes("v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, signed)));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature));
    }

    private static async Task RespondAsync(HttpContext http, Uri? responseUrl, string text, bool replaceOriginal, ILogger logger)
    {
        // Slack response URLs are always on hooks.slack.com (hooks.slack-gov.com for GovSlack); refuse anything else, so a forged payload cannot make the
        // application call arbitrary addresses (the signature already makes forging hard).
        if (responseUrl is not { Scheme: "https", Host: "hooks.slack.com" or "hooks.slack-gov.com" })
        {
            return;
        }

        try
        {
            var client = http.RequestServices.GetService<IHttpClientFactory>()?.CreateClient("Homeostat.Slack") ?? SharedClient;
            var payload = new JsonObject
            {
                ["text"] = text,
                ["replace_original"] = replaceOriginal,
                ["response_type"] = replaceOriginal ? "in_channel" : "ephemeral",
            }.ToJsonString();
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(2)); // Slack expects an answer within 3 seconds.
            using var response = await client.PostAsync(responseUrl, content, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Updating the Slack message failed.");
        }
    }

    /// <summary>A click on an Approve or Reject button.</summary>
    internal sealed record SlackAction(bool Approve, string ApprovalId, string UserId, string UserName, Uri? ResponseUrl)
    {
        public static SlackAction? Parse(string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != "block_actions"
                || !root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array
                || actions.GetArrayLength() == 0
                || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("id", out var userId) || userId.GetString() is not { Length: > 0 } id)
            {
                return null;
            }

            var first = actions[0];
            var actionId = first.TryGetProperty("action_id", out var a) ? a.GetString() : null;
            var approve = actionId == "homeostat_approve";
            if (!approve && actionId != "homeostat_reject")
            {
                return null;
            }

            if (!first.TryGetProperty("value", out var value) || value.GetString() is not { Length: > 0 and <= 200 } approvalId)
            {
                return null;
            }

            var name = user.TryGetProperty("username", out var u) && u.GetString() is { Length: > 0 } username ? username
                : user.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } display ? display
                : id;
            Uri? responseUrl = root.TryGetProperty("response_url", out var r) && Uri.TryCreate(r.GetString(), UriKind.Absolute, out var parsed)
                ? parsed
                : null;
            return new SlackAction(approve, approvalId, id, name, responseUrl);
        }
    }
}

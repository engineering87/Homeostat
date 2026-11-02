using Homeostat.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>Registers the PagerDuty, Opsgenie, Slack and Microsoft Teams notifiers.</summary>
public static class HomeostatIncidentExtensions
{
    /// <summary>
    /// Opens a PagerDuty incident (Events API v2) when healing gives up or a step waits for approval, and resolves it
    /// when the condition clears. One incident per application and condition.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the routing key and, optionally, when incidents are opened.</param>
    /// <example><code>.AddPagerDutyIncidents(o => o.RoutingKey = builder.Configuration["PagerDuty:RoutingKey"])</code></example>
    public static HomeostatBuilder AddPagerDutyIncidents(this HomeostatBuilder builder, Action<PagerDutyOptions> configure)
        => Register<PagerDutyOptions, PagerDutySink>(builder, configure, PagerDutySink.HttpClientName, o => o.Validate());

    /// <summary>
    /// Creates an alert with the Opsgenie alert API when healing gives up or a step waits for approval, and closes it
    /// when the condition clears. The default endpoint is Jira Service Management Operations; set
    /// <see cref="OpsgenieOptions.Url"/> to <see cref="OpsgenieOptions.Opsgenie"/> for standalone Opsgenie.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the API key and, optionally, the endpoint and when alerts are created.</param>
    /// <example><code>.AddOpsgenieAlerts(o => o.ApiKey = builder.Configuration["Jsm:ApiKey"])</code></example>
    public static HomeostatBuilder AddOpsgenieAlerts(this HomeostatBuilder builder, Action<OpsgenieOptions> configure)
        => Register<OpsgenieOptions, OpsgenieSink>(builder, configure, OpsgenieSink.HttpClientName, o => o.Validate());

    /// <summary>
    /// Posts events to a Slack channel through an incoming webhook, formatted with Block Kit. With
    /// <see cref="SlackOptions.ApprovalButtons"/>, approval requests carry Approve and Reject buttons, answered by the
    /// endpoint mapped with <c>MapHomeostatSlackInteractions</c> (package Homeostat.AspNetCore).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the webhook URL and, optionally, the events, buttons and dashboard link.</param>
    public static HomeostatBuilder AddSlackNotifications(this HomeostatBuilder builder, Action<SlackOptions> configure)
        => Register<SlackOptions, SlackSink>(builder, configure, SlackSink.HttpClientName, o => o.Validate());

    /// <summary>
    /// Posts events to a Microsoft Teams channel as Adaptive Cards, through the webhook of a Teams workflow ("post to
    /// a channel when a webhook request is received"). Approval requests link to the dashboard when
    /// <see cref="ChatOptions.DashboardUrl"/> is set.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the webhook URL and, optionally, the events and dashboard link.</param>
    public static HomeostatBuilder AddTeamsNotifications(this HomeostatBuilder builder, Action<TeamsOptions> configure)
        => Register<TeamsOptions, TeamsSink>(builder, configure, TeamsSink.HttpClientName, o => o.Validate());

    private static HomeostatBuilder Register<TOptions, TSink>(
        HomeostatBuilder builder, Action<TOptions> configure, string clientName, Action<TOptions> validate)
        where TOptions : class
        where TSink : Notifications.HomeostatEventSink
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<TOptions>().Configure(configure).ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>>(new ThrowingValidator<TOptions>(validate)));
        // The default HttpClient logging writes the request URL, and webhook URLs carry their secret in the path.
        builder.Services.AddHttpClient(clientName).RemoveAllLoggers();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddEventSink<TSink>();
        return builder;
    }

    private sealed class ThrowingValidator<TOptions>(Action<TOptions> validate) : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            try
            {
                validate(options);
                return ValidateOptionsResult.Success;
            }
            catch (ArgumentException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}

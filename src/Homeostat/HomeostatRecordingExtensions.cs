using Homeostat.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Registers signal recording.</summary>
public static class HomeostatRecordingExtensions
{
    /// <summary>
    /// Records every signal Homeostat receives to daily JSON Lines files, in the background, so they can be replayed
    /// later against new or changed conditions: "what would the plans have done yesterday?" (see <c>HomeostatReplay</c>
    /// in Homeostat.Testing). Signals are queued in memory and the oldest are dropped if the disk falls behind.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Changes the path, sources, retention and details.</param>
    /// <example><code>h.AddSignalRecording(o => o.Path = "/var/log/weather-api/signals-{instance}-{date}.jsonl")</code></example>
    public static HomeostatBuilder AddSignalRecording(this HomeostatBuilder builder, Action<SignalRecordingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<SignalRecordingOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.Path), "SignalRecordingOptions.Path is required.")
            .Validate(o => o.Retention >= TimeSpan.Zero, "SignalRecordingOptions.Retention cannot be negative.")
            .Validate(o => o.BufferCapacity > 0, "SignalRecordingOptions.BufferCapacity must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.TryAddSingleton<SignalRecorder>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SignalRecorder>(sp => sp.GetRequiredService<SignalRecorder>()));
        return builder;
    }
}

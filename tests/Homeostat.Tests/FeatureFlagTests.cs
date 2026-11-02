using Homeostat.FeatureManagement;
using Homeostat.OpenFeature;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Homeostat.Tests;

public class FeatureFlagTests
{
    private static ServiceProvider FeatureServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureManagement:ForecastMaps:EnabledFor:0:Name"] = "Homeostat.Switch",
            ["FeatureManagement:ForecastMaps:EnabledFor:0:Parameters:Switch"] = "shed-forecast-maps",
            ["FeatureManagement:DegradedBanner:EnabledFor:0:Name"] = "Homeostat.Switch",
            ["FeatureManagement:DegradedBanner:EnabledFor:0:Parameters:Switch"] = "shed-forecast-maps",
            ["FeatureManagement:DegradedBanner:EnabledFor:0:Parameters:EnabledWhen"] = "On",
            ["FeatureManagement:Broken:EnabledFor:0:Name"] = "Homeostat.Switch",
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHomeostat();
        services.AddFeatureManagement().AddHomeostatSwitchFilter();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Feature_filter_follows_the_switch()
    {
        await using var services = FeatureServices();
        var features = services.GetRequiredService<IFeatureManager>();
        var switches = services.GetRequiredService<IHomeostatSwitches>();

        Assert.True(await features.IsEnabledAsync("ForecastMaps"));
        Assert.False(await features.IsEnabledAsync("DegradedBanner"));

        switches.TurnOn("shed-forecast-maps");
        Assert.False(await features.IsEnabledAsync("ForecastMaps"));
        Assert.True(await features.IsEnabledAsync("DegradedBanner"));

        switches.TurnOff("shed-forecast-maps");
        Assert.True(await features.IsEnabledAsync("ForecastMaps"));
    }

    [Fact]
    public async Task Feature_filter_without_a_switch_name_is_a_configuration_error()
    {
        await using var services = FeatureServices();
        var features = services.GetRequiredService<IFeatureManager>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => features.IsEnabledAsync("Broken"));
    }

    [Fact]
    public async Task OpenFeature_provider_resolves_switches_as_boolean_flags()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        await using var provider = services.BuildServiceProvider();
        var switches = provider.GetRequiredService<IHomeostatSwitches>();
        var flags = new HomeostatSwitchProvider(switches, "homeostat.");

        var off = await flags.ResolveBooleanValueAsync("homeostat.serve-cached-forecast", true);
        Assert.False(off.Value);
        Assert.Equal("off", off.Variant);

        switches.TurnOn("serve-cached-forecast");
        var on = await flags.ResolveBooleanValueAsync("homeostat.serve-cached-forecast", false);
        Assert.True(on.Value);
        Assert.Equal(Reason.Static, on.Reason);

        var unknown = await flags.ResolveBooleanValueAsync("serve-cached-forecast", false);
        Assert.Equal(ErrorType.FlagNotFound, unknown.ErrorType);
        Assert.False(unknown.Value);

        var text = await flags.ResolveStringValueAsync("homeostat.serve-cached-forecast", "fallback");
        Assert.Equal(ErrorType.TypeMismatch, text.ErrorType);
        Assert.Equal("fallback", text.Value);

        // Not ours: a multi-provider moves on to the next provider.
        var other = await flags.ResolveStringValueAsync("forecast-units", "metric");
        Assert.Equal(ErrorType.FlagNotFound, other.ErrorType);
    }

    [Fact]
    public async Task OpenFeature_client_sees_switches_and_change_events()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        await using var provider = services.BuildServiceProvider();
        var switches = provider.GetRequiredService<IHomeostatSwitches>();
        var domain = $"homeostat-{Guid.NewGuid():N}";

        var changed = new TaskCompletionSource<ProviderEventPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Api.Instance.SetProviderAsync(domain, new HomeostatSwitchProvider(switches));
        var client = Api.Instance.GetClient(domain);
        client.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, payload => changed.TrySetResult(payload));

        Assert.False(await client.GetBooleanValueAsync("shed-forecast-maps", false));
        switches.TurnOn("shed-forecast-maps");
        Assert.True(await client.GetBooleanValueAsync("shed-forecast-maps", false));

        var payload = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["shed-forecast-maps"], payload!.FlagsChanged!);
    }
}

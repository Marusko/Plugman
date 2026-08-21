using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugman.Contracts;
using Plugman.Contracts.Blazor;
using Plugman.Core;

namespace Plugman.Tests.Blazor;

/// <summary>
/// Renders the real SampleBlazorPlugin out of a collectible load context, the same way the
/// Blazor Server sample host does — through <see cref="DynamicComponent"/>, with a component
/// type the host never referenced at compile time.
/// </summary>
public class BlazorUiPluginTests
{
    private const string PluginId = "com.plugman.sample.blazor";

    [Fact]
    public async Task A_blazor_plugin_loads_in_a_blazor_capable_host()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync(PluginId);

        Assert.True(descriptor.IsLoaded, descriptor.LoadError?.Message);
        Assert.Equal(["blazor"], descriptor.UiCapabilities);
    }

    /// <summary>
    /// The Blazor half of the cross-context identity check: the component type produced inside
    /// the plugin must satisfy the host's own <see cref="IComponent"/>, or the renderer would
    /// reject it despite the names matching.
    /// </summary>
    [Fact]
    public async Task The_component_type_implements_the_hosts_IComponent()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var componentType = host.Manager.TryInvoke<IBlazorUiPlugin, Type>(PluginId, p => p.ComponentType);
        Assert.True(componentType.Success, componentType.Error?.Message);

        Assert.True(typeof(IComponent).IsAssignableFrom(componentType.Value));
        Assert.NotSame(typeof(IComponent).Assembly, componentType.Value!.Assembly);

        var plugin = host.Manager.GetPlugin<IBlazorUiPlugin>(PluginId);
        var implemented = plugin!.GetType().GetInterfaces().Single(i => i.FullName == typeof(IBlazorUiPlugin).FullName);
        Assert.Same(typeof(IBlazorUiPlugin), implemented);
        Assert.Same(typeof(IPlugin), plugin.GetType().GetInterfaces().Single(i => i.FullName == typeof(IPlugin).FullName));
    }

    [Fact]
    public async Task The_plugin_component_renders_through_DynamicComponent()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var html = await RenderAsync(host, PluginId);

        Assert.Contains("Sample Blazor Plugin", html);
        Assert.Contains("SampleBlazorPlugin.dll", html);
        Assert.Contains("Clicked 0 time(s)", html);
    }

    [Fact]
    public async Task Default_parameters_reach_the_component()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var html = await RenderAsync(host, PluginId, new Dictionary<string, object?> { ["Title"] = "Overridden title" });

        Assert.Contains("Overridden title", html);
        Assert.DoesNotContain("<h3>Sample Blazor Plugin</h3>", html);
    }

    [Fact]
    public async Task A_blazor_plugin_is_refused_by_a_host_without_the_capability()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync(PluginId);

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.HostCapability, descriptor.LoadError!.Stage);
    }

    /// <summary>
    /// Renders one plugin component exactly like the sample host does: a DynamicComponent
    /// pointed at the plugin's type, with the plugin's own default parameters.
    /// </summary>
    private static async Task<string> RenderAsync(
        TestHost host,
        string pluginId,
        IReadOnlyDictionary<string, object?>? parameterOverrides = null)
    {
        var componentType = host.Manager.TryInvoke<IBlazorUiPlugin, Type>(pluginId, p => p.ComponentType).Value!;
        var parameters = parameterOverrides
            ?? host.Manager.TryInvoke<IBlazorUiPlugin, IReadOnlyDictionary<string, object?>?>(
                pluginId, p => p.DefaultParameters).Value;

        var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<DynamicComponent>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(DynamicComponent.Type)] = componentType,
                    [nameof(DynamicComponent.Parameters)] = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value)
                }));

            return output.ToHtmlString();
        });
    }
}

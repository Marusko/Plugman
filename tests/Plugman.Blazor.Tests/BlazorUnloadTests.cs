using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugman.Contracts.Blazor;
using Plugman.Core;

namespace Plugman.Tests.Blazor;

/// <summary>
/// What can and cannot be collected in a Blazor host, established by experiment:
/// <list type="bullet">
/// <item>A Blazor plugin that was loaded, listed and disabled unloads completely.</item>
/// <item>Reading <see cref="IBlazorUiPlugin.ComponentType"/> does not pin anything.</item>
/// <item>
/// Rendering the component does: Blazor's component activation caches per-component-type
/// metadata in a process-wide static, so the plugin assembly stays alive until the process
/// ends. Plugman still shuts the plugin down and stops rendering it; the memory is not
/// reclaimed.
/// </item>
/// </list>
/// </summary>
public class BlazorUnloadTests
{
    private const string PluginId = "com.plugman.sample.blazor";

    [Fact]
    public async Task Disabling_a_blazor_plugin_that_was_never_rendered_unloads_it_completely()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);
        await host.Manager.DisableAsync(PluginId);

        Assert.True(await host.Manager.WaitForUnloadAsync(PluginId, TimeSpan.FromSeconds(10)));
        Assert.False(folder.ReadManifest("SampleBlazorPlugin")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Listing_a_plugins_component_type_does_not_pin_it()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        ReadComponentType(host);

        await host.Manager.DisableAsync(PluginId);

        Assert.True(
            await host.Manager.WaitForUnloadAsync(PluginId, TimeSpan.FromSeconds(10)),
            "Reading ComponentType should not keep the plugin's load context alive.");
    }

    [Fact]
    public async Task A_logic_plugin_unloads_completely_inside_a_blazor_process()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");
        await host.Manager.DisableAsync("fixture.good");

        Assert.True(await host.Manager.WaitForUnloadAsync("fixture.good", TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// Documents the Blazor limitation as an executable fact, so a future framework change (or
    /// a regression in how we release plugins) shows up as a test result rather than folklore.
    /// </summary>
    [Fact]
    public async Task Rendering_a_component_pins_its_plugin_assembly_for_the_life_of_the_process()
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleBlazorPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        await RenderAndReleaseAsync(host);

        await host.Manager.DisableAsync(PluginId);

        Assert.False(
            await host.Manager.WaitForUnloadAsync(PluginId, TimeSpan.FromSeconds(3)),
            "Blazor no longer pins rendered component types — update the README.");

        // The plugin is still shut down, disabled and no longer renderable.
        Assert.Null(host.Manager.GetPlugin<IBlazorUiPlugin>(PluginId));
        Assert.False(host.Manager.GetDescriptor(PluginId)!.IsLoaded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadComponentType(TestHost host)
    {
        var componentType = host.Manager.TryInvoke<IBlazorUiPlugin, Type>(PluginId, p => p.ComponentType);

        Assert.True(componentType.Success);
        Assert.NotNull(componentType.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RenderAndReleaseAsync(TestHost host)
    {
        var componentType = host.Manager.TryInvoke<IBlazorUiPlugin, Type>(PluginId, p => p.ComponentType).Value!;

        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<DynamicComponent>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(DynamicComponent.Type)] = componentType
                }));

            return output.ToHtmlString();
        });

        Assert.Contains("Sample Blazor Plugin", html);
    }
}

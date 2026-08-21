using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Plugman.Contracts.Wpf;
using Plugman.Core;

namespace Plugman.Tests.Wpf;

/// <summary>
/// What can and cannot be collected in a WPF process, established by experiment rather than
/// by assumption. The short version:
/// <list type="bullet">
/// <item>Logic plugins and un-rendered WPF plugins unload completely.</item>
/// <item>A WPF plugin whose view is built in code unloads completely, view included.</item>
/// <item>
/// A WPF plugin whose view comes from compiled XAML does not: loading BAML out of a plugin
/// assembly makes WPF hold that assembly for the life of the process. Plugman still shuts the
/// plugin down and releases everything it owns, but the memory is not reclaimed.
/// </item>
/// </list>
/// </summary>
public class WpfUnloadTests
{
    private const string XamlPluginId = "com.plugman.sample.wpf";
    private const string CodeViewPluginId = "fixture.wpf.codeview";

    [Fact]
    public void A_wpf_plugin_that_was_never_rendered_unloads_completely() => Sta.Run(async () =>
    {
        // Uses the code-built fixture rather than the XAML sample on purpose: once any context
        // in this process has loaded BAML out of an assembly, every later context loading an
        // assembly with that identity is pinned too, so a XAML plugin here would pass or fail
        // depending on which tests ran before it.
        using var folder = new TestPluginsFolder();
        folder.AddFixture("WpfCodeViewPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(CodeViewPluginId);
        await host.Manager.DisableAsync(CodeViewPluginId);

        Assert.True(await host.Manager.WaitForUnloadAsync(CodeViewPluginId, TimeSpan.FromSeconds(10)));
    });

    [Fact]
    public void A_logic_plugin_unloads_completely_inside_a_wpf_process() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");
        await host.Manager.DisableAsync("fixture.good");

        Assert.True(await host.Manager.WaitForUnloadAsync("fixture.good", TimeSpan.FromSeconds(10)));
    });

    /// <summary>
    /// The full disable cycle for a WPF UI plugin: build the view, host it, take it back out of
    /// the visual tree, disable — and the load context, the view and the plugin all go away.
    /// </summary>
    [Fact]
    public void A_code_built_wpf_view_unloads_once_it_leaves_the_visual_tree() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("WpfCodeViewPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(CodeViewPluginId);

        var viewProbe = BuildHostAndRelease(host, CodeViewPluginId);

        await host.Manager.DisableAsync(CodeViewPluginId);

        Assert.True(
            await host.Manager.WaitForUnloadAsync(CodeViewPluginId, TimeSpan.FromSeconds(15)),
            "The plugin's load context stayed alive after its view left the visual tree.");

        Assert.False(viewProbe.IsAlive, "The plugin's view was not collected.");
        Assert.False(folder.ReadManifest("WpfCodeViewPlugin")["enabled"]!.GetValue<bool>());
    });

    /// <summary>
    /// Documents the WPF limitation as an executable fact. If a future WPF release stops
    /// pinning assemblies whose BAML it has loaded, this test fails and the README paragraph
    /// about it needs deleting — which is exactly the notification we want.
    /// </summary>
    [Fact]
    public void A_xaml_built_view_pins_its_plugin_assembly_for_the_life_of_the_process() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(XamlPluginId);

        BuildHostAndRelease(host, XamlPluginId);

        await host.Manager.DisableAsync(XamlPluginId);

        Assert.False(
            await host.Manager.WaitForUnloadAsync(XamlPluginId, TimeSpan.FromSeconds(3)),
            "WPF no longer pins plugin assemblies whose compiled XAML it loaded — update the README.");

        // The plugin is still fully shut down and disabled; only the memory is not reclaimed.
        Assert.Null(host.Manager.GetPlugin<IWpfUiPlugin>(XamlPluginId));
        Assert.False(host.Manager.GetDescriptor(XamlPluginId)!.IsLoaded);

        // And the pin follows the assembly identity, not the load context: a second, fresh
        // context loading the same plugin assembly is stuck too, even without rendering it.
        // This is why a WPF host should treat "disable" as "stop running it", not as
        // "reclaim its memory".
        using var secondFolder = new TestPluginsFolder();
        secondFolder.AddSamplePlugin("SampleWpfPlugin");

        await using var secondHost = new TestHost(secondFolder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await secondHost.Manager.ScanAsync();
        await secondHost.Manager.LoadAsync(XamlPluginId);
        await secondHost.Manager.DisableAsync(XamlPluginId);

        Assert.False(await secondHost.Manager.WaitForUnloadAsync(XamlPluginId, TimeSpan.FromSeconds(3)));
    });

    /// <summary>
    /// Builds a view, puts it through a real layout pass, then removes it — leaving no strong
    /// reference in the caller's frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildHostAndRelease(TestHost host, string pluginId)
    {
        var context = host.Manager.GetPluginContext(pluginId)!;
        var created = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(pluginId, p => p.CreateView(context));
        Assert.True(created.Success, created.Error?.Message);

        var container = new ContentControl { Content = created.Value };
        container.Measure(new Size(800, 600));
        container.Arrange(new Rect(0, 0, 800, 600));
        container.UpdateLayout();

        var probe = new WeakReference(created.Value, trackResurrection: true);

        container.Content = null;
        container.UpdateLayout();

        return probe;
    }
}

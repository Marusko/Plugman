using System.Windows;
using System.Windows.Controls;
using Plugman.Contracts;
using Plugman.Contracts.Wpf;
using Plugman.Core;

namespace Plugman.Tests.Wpf;

/// <summary>
/// Exercises the real SampleWpfPlugin in a WPF-capable process: view creation, cross-context
/// type identity, layout (which forces the plugin's compiled XAML to load), and unloading.
/// </summary>
public class WpfUiPluginTests
{
    private const string PluginId = "com.plugman.sample.wpf";

    [Fact]
    public void A_wpf_plugin_loads_in_a_wpf_capable_host() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync(PluginId);

        Assert.True(descriptor.IsLoaded, descriptor.LoadError?.Message);
        Assert.Equal(["wpf"], descriptor.UiCapabilities);
    });

    /// <summary>
    /// The acceptance criterion: the element the plugin builds drops into the host's world with
    /// no InvalidCastException from duplicated types. If PresentationFramework or
    /// Plugman.Contracts.Wpf had loaded a second time inside the plugin's context, the cast to
    /// FrameworkElement here would throw despite the names matching.
    /// </summary>
    [Fact]
    public void The_view_a_plugin_builds_is_the_hosts_FrameworkElement_type() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var context = host.Manager.GetPluginContext(PluginId);
        Assert.NotNull(context);

        var created = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(
            PluginId,
            plugin => plugin.CreateView(context!));

        Assert.True(created.Success, created.Error?.Message);

        var view = created.Value!;
        Assert.IsAssignableFrom<FrameworkElement>(view);
        Assert.IsAssignableFrom<UserControl>(view);

        // The plugin's own type lives in the isolated context...
        Assert.NotSame(typeof(FrameworkElement).Assembly, view.GetType().Assembly);

        // ...while every type it inherits from is the host's.
        Assert.Same(typeof(UserControl), view.GetType().BaseType);
        Assert.Same(typeof(FrameworkElement).Assembly, typeof(UserControl).Assembly);
    });

    [Fact]
    public void The_plugins_compiled_xaml_loads_and_lays_out_inside_the_host() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var context = host.Manager.GetPluginContext(PluginId)!;
        var view = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(PluginId, p => p.CreateView(context)).Value!;

        // Host the plugin's element exactly like the sample host does, then force a layout pass.
        // This is what actually proves the plugin's BAML resolved: InitializeComponent has run,
        // the visual tree has content, and the bindings evaluated.
        var container = new ContentControl { Content = view };
        container.Measure(new Size(800, 600));
        container.Arrange(new Rect(0, 0, 800, 600));
        container.UpdateLayout();

        Assert.True(view.ActualWidth > 0, "The plugin view did not lay out; its XAML probably failed to load.");
        Assert.True(System.Windows.Media.VisualTreeHelper.GetChildrenCount(view) > 0, "The plugin view has no visual children.");
        Assert.NotNull(view.DataContext);
        Assert.Equal("SampleWpfPlugin.SampleViewModel", view.DataContext!.GetType().FullName);

        container.Content = null;
    });

    [Fact]
    public void A_view_title_is_readable_through_the_capability_interface() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var title = host.Manager.TryInvoke<IWpfUiPlugin, string>(PluginId, plugin => plugin.ViewTitle);

        Assert.True(title.Success);
        Assert.Equal("Sample WPF", title.Value);
    });

    [Fact]
    public void The_capability_interface_has_one_type_identity_across_contexts() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var plugin = host.Manager.GetPlugin<IWpfUiPlugin>(PluginId);
        Assert.NotNull(plugin);

        var implemented = plugin!.GetType().GetInterfaces().Single(i => i.FullName == typeof(IWpfUiPlugin).FullName);
        Assert.Same(typeof(IWpfUiPlugin), implemented);
        Assert.Same(typeof(IPlugin), plugin.GetType().GetInterfaces().Single(i => i.FullName == typeof(IPlugin).FullName));
    });

    /// <summary>
    /// A plugin that throws while building its UI is caught at the manager boundary: the host
    /// gets a failure it can render as an error panel, and the failure is recorded on the
    /// descriptor. The sample WPF host does exactly this — it shows a tab with the message.
    /// </summary>
    [Fact]
    public void A_plugin_that_throws_while_building_its_view_does_not_crash_the_host() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("WpfCodeViewPlugin", folderName: "ThrowingView", configureManifest: manifest =>
        {
            manifest["id"] = "fixture.wpf.throwingview";
            manifest["entryType"] = "WpfCodeViewPlugin.ThrowingViewPlugin";
        });

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.wpf.throwingview");

        var context = host.Manager.GetPluginContext("fixture.wpf.throwingview")!;
        var created = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(
            "fixture.wpf.throwingview",
            plugin => plugin.CreateView(context));

        Assert.False(created.Success);
        Assert.Equal(PluginLoadStage.Capability, created.Error!.Stage);
        Assert.Contains("fixture view construction failure", created.Error.Message);

        // Still loaded, still listed, and the failure is visible to the host.
        var descriptor = host.Manager.GetDescriptor("fixture.wpf.throwingview")!;
        Assert.True(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Capability, descriptor.LoadError!.Stage);
    });

    [Fact]
    public void Disabling_a_wpf_plugin_persists_the_state_and_tears_the_plugin_down() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var descriptor = await host.Manager.DisableAsync(PluginId);

        Assert.False(descriptor.IsLoaded);
        Assert.False(descriptor.IsEnabled);
        Assert.Null(host.Manager.GetPlugin<IWpfUiPlugin>(PluginId));
        Assert.False(folder.ReadManifest("SampleWpfPlugin")["enabled"]!.GetValue<bool>());
    });
}

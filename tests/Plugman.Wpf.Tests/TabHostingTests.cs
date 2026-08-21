using System.Windows;
using System.Windows.Controls;
using Plugman.Contracts.Wpf;
using Plugman.Core;

namespace Plugman.Tests.Wpf;

/// <summary>
/// Mirrors what the sample WPF host actually does: a live window whose TabControl is empty at
/// startup and gets a plugin tab added later, once scanning and loading have finished.
/// </summary>
/// <remarks>
/// Hosting the view in a plain ContentControl — which is how the other tests check it — passes
/// even when the real host renders a blank panel, because a ContentControl has no notion of
/// selection. A TabControl populated at runtime keeps SelectedIndex at -1, and an unselected
/// tab never realizes its content.
/// </remarks>
public class TabHostingTests
{
    private const string PluginId = "com.plugman.sample.wpf";

    [Fact]
    public void A_plugin_view_added_to_a_live_TabControl_is_actually_displayed() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        // The window goes up first, with nothing in it — exactly like the sample host.
        var tabs = new TabControl();
        var window = new Window { Width = 900, Height = 600, Content = tabs };
        window.Show();
        window.UpdateLayout();

        var context = host.Manager.GetPluginContext(PluginId)!;
        var created = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(PluginId, p => p.CreateView(context));
        Assert.True(created.Success, created.Error?.Message);

        var tab = new TabItem { Header = "Sample WPF", Tag = PluginId, Content = created.Value };
        tabs.Items.Add(tab);

        if (tabs.SelectedIndex < 0)
            tabs.SelectedItem = tab;

        window.UpdateLayout();
        Pump();

        var view = created.Value!;

        try
        {
            Assert.True(tabs.SelectedIndex >= 0, "Nothing is selected, so the tab's content is never realized.");
            Assert.True(view.IsLoaded, "The plugin view never loaded.");
            Assert.True(view.IsVisible, "The plugin view is in the tab but not visible.");
            Assert.True(view.ActualWidth > 0 && view.ActualHeight > 0, $"The plugin view has no size: {view.ActualWidth}x{view.ActualHeight}.");

            // The panel's own content, not just the container: the button and the status line
            // the plugin's XAML declares.
            var buttons = Descendants(view).OfType<Button>().ToArray();
            Assert.Single(buttons);
            Assert.Equal("Tick", buttons[0].Content);

            var texts = Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Sample WPF Plugin", texts);
            Assert.Contains("Ready.", texts);
        }
        finally
        {
            tab.Content = null;
            window.Close();
        }
    });

    /// <summary>Clicking the plugin's button updates its own view model and its own view.</summary>
    [Fact]
    public void The_plugins_button_updates_the_panel() => Sta.Run(async () =>
    {
        using var folder = new TestPluginsFolder();
        folder.AddSamplePlugin("SampleWpfPlugin");

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync(PluginId);

        var context = host.Manager.GetPluginContext(PluginId)!;
        var view = host.Manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(PluginId, p => p.CreateView(context)).Value!;

        var tabs = new TabControl();
        var window = new Window { Width = 900, Height = 600, Content = tabs };
        window.Show();

        var tab = new TabItem { Content = view };
        tabs.Items.Add(tab);
        tabs.SelectedItem = tab;
        window.UpdateLayout();
        Pump();

        try
        {
            var button = Descendants(view).OfType<Button>().Single();
            var status = () => Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToArray();

            Assert.Contains("Ready.", status());

            Click(button);
            Assert.Contains(status(), text => text.StartsWith("Ticked 1 time(s) at ", StringComparison.Ordinal));

            Click(button);
            Assert.Contains(status(), text => text.StartsWith("Ticked 2 time(s) at ", StringComparison.Ordinal));
        }
        finally
        {
            tab.Content = null;
            window.Close();
        }
    });

    /// <summary>
    /// Clicks through the automation peer rather than raising ButtonBase.ClickEvent directly:
    /// a raised Click event skips ButtonBase.OnClick, so the button's bound Command — which is
    /// what the plugin's view model listens to — would never run.
    /// </summary>
    private static void Click(Button button)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Invoke);

        invoke.Invoke();
        Pump();
    }

    /// <summary>
    /// Runs the dispatcher queue to completion. Layout alone is not enough: Loaded and the
    /// binding updates that follow a command are queued dispatcher operations, and a test that
    /// never pumps sees a half-built visual tree.
    /// </summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));

        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}

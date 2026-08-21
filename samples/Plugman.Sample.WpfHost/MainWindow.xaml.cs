using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugman.Contracts.Wpf;
using Plugman.Core;
using Plugman.Samples;

namespace Plugman.Sample.WpfHost;

/// <summary>
/// WPF host. The startup sequence is identical to the console and Blazor hosts; the only
/// difference is that this one also consumes the <see cref="IWpfUiPlugin"/> capability.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<PluginRow> _rows = [];
    private readonly ILoggerFactory _loggerFactory;
    private readonly PluginManager _manager;

    public MainWindow()
    {
        InitializeComponent();

        _loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));

        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();

        _manager = new PluginManager(SamplePaths.ResolvePluginsRoot(), services, _loggerFactory);
        _manager.PluginStateChanged += (_, e) =>
            Dispatcher.BeginInvoke(() => StatusText.Text = $"{e.PluginId}: {e.State}");

        PluginList.ItemsSource = _rows;

        Loaded += OnLoadedAsync;
        Closed += OnClosed;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        await _manager.ScanAsync();
        await _manager.LoadEnabledAsync();

        StatusText.Text = $"Plugins root: {_manager.PluginsRootDirectory} — host capabilities: " +
                          $"{string.Join(", ", _manager.HostUiCapabilities)}";

        Refresh();
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        await _manager.ScanAsync();
        await _manager.LoadEnabledAsync();
        Refresh();
    }

    private async void OnEnableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pluginId })
            return;

        var descriptor = await _manager.EnableAsync(pluginId);
        if (descriptor.LoadError is { } error)
            MessageBox.Show(this, error.Message, "Plugin failed to load", MessageBoxButton.OK, MessageBoxImage.Warning);

        Refresh();
    }

    private async void OnDisableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pluginId })
            return;

        // Order matters. The plugin's view must leave the visual tree *before* the load context
        // is unloaded: a FrameworkElement built by the plugin is a live reference into that
        // context, and WPF keeps its own references to anything in a visual tree.
        RemoveTab(pluginId);

        await _manager.DisableAsync(pluginId);

        var collected = await _manager.WaitForUnloadAsync(pluginId, TimeSpan.FromSeconds(5));
        StatusText.Text = collected
            ? $"{pluginId} disabled; load context collected."
            : $"{pluginId} disabled, but its load context is still alive — something still references it.";

        Refresh();
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (var descriptor in _manager.DiscoveredPlugins.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            _rows.Add(new PluginRow(
                descriptor.Id,
                descriptor.Metadata.Name,
                $"v{descriptor.Metadata.Version} · {(descriptor.UiCapabilities.Count == 0 ? "logic only" : string.Join("+", descriptor.UiCapabilities))} · " +
                $"{(descriptor.IsEnabled ? "enabled" : "disabled")}/{(descriptor.IsLoaded ? "loaded" : "not loaded")}",
                descriptor.LoadError?.Message ?? string.Empty));
        }

        SyncTabs();
    }

    /// <summary>Adds a tab for every loaded WPF plugin and removes tabs for the rest.</summary>
    private void SyncTabs()
    {
        var loadedWpfPluginIds = _manager.DiscoveredPlugins
            .Where(p => p.IsLoaded && p.UiCapabilities.Contains(PluginUiCapability.Wpf))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in PluginTabs.Items.OfType<TabItem>().ToArray())
        {
            if (tab.Tag is string id && !loadedWpfPluginIds.Contains(id))
                PluginTabs.Items.Remove(tab);
        }

        var existing = PluginTabs.Items.OfType<TabItem>()
            .Select(t => t.Tag as string)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var pluginId in loadedWpfPluginIds.Where(id => !existing.Contains(id)))
            AddTab(pluginId);
    }

    private void AddTab(string pluginId)
    {
        var context = _manager.GetPluginContext(pluginId);
        if (context is null)
            return;

        // CreateView runs plugin code, so it goes through the manager boundary: a plugin that
        // throws while building its UI must not take the window down with it.
        var title = _manager.TryInvoke<IWpfUiPlugin, string>(pluginId, plugin => plugin.ViewTitle);
        var view = _manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(pluginId, plugin => plugin.CreateView(context));

        if (!view.Success || view.Value is null)
        {
            PluginTabs.Items.Add(new TabItem
            {
                Header = title.Value ?? pluginId,
                Tag = pluginId,
                Content = new TextBlock
                {
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"This plugin failed to build its view:\n\n{view.Error?.Message}"
                }
            });
            return;
        }

        // The cast the acceptance criteria care about: view.Value is a FrameworkElement built
        // inside the plugin's load context, and it drops straight into the host's visual tree.
        // That only works because PresentationFramework resolved from the host for both of us.
        PluginTabs.Items.Add(new TabItem
        {
            Header = title.Value ?? pluginId,
            Tag = pluginId,
            Content = view.Value
        });
    }

    private void RemoveTab(string pluginId)
    {
        foreach (var tab in PluginTabs.Items.OfType<TabItem>().ToArray())
        {
            if (tab.Tag as string != pluginId)
                continue;

            tab.Content = null;
            PluginTabs.Items.Remove(tab);
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _manager.DisposeAsync();
        _loggerFactory.Dispose();
    }

    /// <summary>Plain row model for the plugin list; holds no plugin types.</summary>
    private sealed record PluginRow(string Id, string Name, string Detail, string Status);
}

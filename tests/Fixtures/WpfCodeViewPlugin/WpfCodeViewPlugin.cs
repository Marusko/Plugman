using System.Windows;
using System.Windows.Controls;
using Plugman.Contracts;
using Plugman.Contracts.Wpf;

namespace WpfCodeViewPlugin;

/// <summary>
/// A WPF plugin whose view is built in code instead of compiled XAML. Functionally the same
/// as the XAML sample, but nothing loads BAML out of this assembly — which is what lets its
/// load context be collected after the view is released.
/// </summary>
public sealed class WpfCodeViewPlugin : IWpfUiPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.wpf.codeview",
        Name: "Code-built WPF Fixture Plugin",
        Version: "1.0.0",
        Description: "Builds its WPF view in code.",
        Author: "tests");

    public string ViewTitle => "Code-built view";

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public FrameworkElement CreateView(IPluginContext context)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Built in code by the plugin.", FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = context.PluginDataDirectory });
        panel.Children.Add(new Button { Content = "Click", Padding = new Thickness(12, 4, 12, 4) });

        return new UserControl { Content = panel };
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Fails while building its view. Used to prove that a plugin throwing during UI construction
/// is caught at the manager boundary instead of taking the window down.
/// </summary>
public sealed class ThrowingViewPlugin : IWpfUiPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.wpf.throwingview",
        Name: "Throwing View Fixture Plugin",
        Version: "1.0.0",
        Description: "Throws from CreateView.",
        Author: "tests");

    public string ViewTitle => "Never renders";

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public FrameworkElement CreateView(IPluginContext context) =>
        throw new InvalidOperationException("fixture view construction failure");

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

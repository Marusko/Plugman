using System.Windows;
using Microsoft.Extensions.Logging;
using Plugman.Contracts;
using Plugman.Contracts.Wpf;

namespace SampleWpfPlugin;

/// <summary>
/// A plugin with a WPF panel: <see cref="IPlugin"/> plus the <see cref="IWpfUiPlugin"/>
/// capability. Loadable only in a host that supports the "wpf" capability; anywhere else the
/// manager refuses it from the manifest alone, before touching the assembly.
/// </summary>
public sealed class SampleWpfPlugin : IWpfUiPlugin
{
    private IPluginContext? _context;

    public PluginMetadata Metadata { get; } = new(
        Id: "com.plugman.sample.wpf",
        Name: "Sample WPF Plugin",
        Version: "1.0.0",
        Description: "Renders a self-contained WPF panel with its own view model.",
        Author: "Plugman samples");

    public string ViewTitle => "Sample WPF";

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        context.Logger.LogInformation("Sample WPF plugin initialized.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds view and view model together. The host receives a FrameworkElement and hosts it;
    /// it never learns what is inside.
    /// </summary>
    public FrameworkElement CreateView(IPluginContext context)
    {
        context.Logger.LogInformation("Building the sample WPF view.");
        return new SampleView(new SampleViewModel(context));
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _context?.Logger.LogInformation("Sample WPF plugin shutting down.");
        _context = null;
        return Task.CompletedTask;
    }
}

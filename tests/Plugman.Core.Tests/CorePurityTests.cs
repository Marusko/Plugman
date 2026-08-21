using Plugman.Core;

namespace Plugman.Tests.Core;

/// <summary>
/// Guards the package split: Plugman.Core must stay usable from a console or worker host with
/// no UI framework anywhere in its closure.
/// </summary>
public class CorePurityTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "Microsoft.AspNetCore",
        "Plugman.Contracts.Wpf",
        "Plugman.Contracts.Blazor"
    ];

    [Fact]
    public void Plugman_Core_references_no_ui_framework_assemblies()
    {
        var referenced = typeof(PluginManager).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var offenders = referenced
            .Where(name => ForbiddenAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Plugman_Contracts_references_no_ui_framework_assemblies()
    {
        var referenced = typeof(Contracts.IPlugin).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referenced, name =>
            ForbiddenAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Plugman_Core_only_depends_on_the_contracts_package()
    {
        var pluginmanReferences = typeof(PluginManager).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Plugman", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(["Plugman.Contracts"], pluginmanReferences);
    }

    [Fact]
    public void The_core_knows_the_ui_capability_names_without_referencing_the_packages()
    {
        // The manager reasons about "wpf"/"blazor" as strings from the manifest; that is the
        // whole reason it can validate them without a compile-time dependency.
        Assert.Equal(["wpf", "blazor"], PluginUiCapability.Known);
        Assert.True(PluginUiCapability.IsKnown("WPF"));
        Assert.False(PluginUiCapability.IsKnown("winforms"));
    }
}

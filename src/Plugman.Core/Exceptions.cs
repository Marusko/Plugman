namespace Plugman.Core;

/// <summary>Thrown when a <c>plugin.json</c> is missing, malformed or incomplete.</summary>
public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string manifestPath, string message, Exception? inner = null)
        : base(message, inner)
    {
        ManifestPath = manifestPath;
    }

    public string ManifestPath { get; }
}

/// <summary>Thrown when an operation names a plugin id that was never discovered.</summary>
public sealed class PluginNotFoundException : Exception
{
    public PluginNotFoundException(string pluginId)
        : base($"No plugin with id '{pluginId}' has been discovered. Call ScanAsync first.")
    {
        PluginId = pluginId;
    }

    public string PluginId { get; }
}

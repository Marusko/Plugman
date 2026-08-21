using Microsoft.Extensions.Logging;
using Plugman.Contracts;

namespace Plugman.Core;

/// <summary>Default <see cref="IPluginContext"/>, one instance per loaded plugin.</summary>
internal sealed class PluginContext(ILogger logger, IServiceProvider services, string pluginDataDirectory)
    : IPluginContext
{
    public ILogger Logger { get; } = logger;

    public IServiceProvider Services { get; } = services;

    public string PluginDataDirectory { get; } = pluginDataDirectory;
}

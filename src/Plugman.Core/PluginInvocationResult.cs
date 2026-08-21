namespace Plugman.Core;

/// <summary>
/// Outcome of a plugin call made through the manager boundary.
/// </summary>
/// <remarks>
/// Capability calls are the one place a host can be tempted to call plugin code directly.
/// Going through <see cref="PluginManager.TryInvoke{TPlugin, TResult}"/> or
/// <see cref="PluginManager.InvokeAsync{TPlugin, TResult}"/> keeps the try/catch, the logging
/// and the contextual-reflection scope in one place, and turns a throwing plugin into a
/// recorded <see cref="PluginLoadError"/> instead of a dead host.
/// </remarks>
public sealed record PluginInvocationResult<T>(bool Success, T? Value, PluginLoadError? Error)
{
    public static PluginInvocationResult<T> Ok(T value) => new(true, value, null);

    public static PluginInvocationResult<T> Fail(PluginLoadError error) => new(false, default, error);
}

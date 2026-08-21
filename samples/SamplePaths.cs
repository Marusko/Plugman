using System.IO;
using System.Reflection;

namespace Plugman.Samples;

/// <summary>
/// Locates the repository's <c>/plugins</c> folder so the samples can be run straight from
/// their build output (or from an IDE) without copying plugins around.
/// </summary>
/// <remarks>
/// A real host would simply use a folder next to its own executable, or a path from
/// configuration. This lookup exists only so all three sample hosts share one runtime
/// plugins folder.
/// </remarks>
internal static class SamplePaths
{
    public static string ResolvePluginsRoot()
    {
        if (Environment.GetEnvironmentVariable("PLUGMAN_PLUGINS_DIR") is { Length: > 0 } fromEnvironment)
            return Path.GetFullPath(fromEnvironment);

        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Plugman.slnx")))
                return Path.Combine(directory.FullName, "plugins");

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "plugins");
    }
}

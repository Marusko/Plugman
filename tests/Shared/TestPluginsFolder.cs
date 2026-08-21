using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugman.Tests;

/// <summary>
/// A throwaway plugins root for one test: fixture plugin folders are copied in from
/// <c>/artifacts/test-plugins</c> so a test can freely rewrite manifests, add folders while
/// the manager is running, or corrupt them on purpose.
/// </summary>
internal sealed class TestPluginsFolder : IDisposable
{
    private static readonly Lazy<string> RepositoryRoot = new(LocateRepositoryRoot);

    private static readonly Lazy<string> FixtureSource =
        new(() => Path.Combine(RepositoryRoot.Value, "artifacts", "test-plugins"));

    public TestPluginsFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "plugman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The temp plugins root handed to the manager.</summary>
    public string Root { get; }

    /// <summary>
    /// Copies a built fixture plugin folder in, optionally rewriting its manifest.
    /// </summary>
    /// <param name="fixtureName">Fixture project name, e.g. <c>GoodPlugin</c>.</param>
    /// <param name="folderName">Destination folder name; defaults to the fixture name.</param>
    /// <param name="configureManifest">Hook to edit plugin.json before the manager ever sees it.</param>
    public string AddFixture(string fixtureName, string? folderName = null, Action<JsonObject>? configureManifest = null)
    {
        var source = Path.Combine(FixtureSource.Value, fixtureName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Fixture '{fixtureName}' was not found at {source}. Build the tests/Fixtures projects first.");
        }

        var destination = Path.Combine(Root, folderName ?? fixtureName);
        CopyDirectory(source, destination);

        if (configureManifest is not null)
            EditManifest(destination, configureManifest);

        return destination;
    }

    /// <summary>
    /// Copies one of the built sample plugins from the repository's runtime <c>/plugins</c>
    /// folder. Used by the UI tests, which are worth running against the real sample plugins
    /// rather than against a stand-in.
    /// </summary>
    public string AddSamplePlugin(string sampleName, string? folderName = null, Action<JsonObject>? configureManifest = null)
    {
        var source = Path.Combine(RepositoryRoot.Value, "plugins", sampleName);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Sample plugin '{sampleName}' was not found at {source}. Build the samples first.");

        var destination = Path.Combine(Root, folderName ?? sampleName);
        CopyDirectory(source, destination);

        if (configureManifest is not null)
            EditManifest(destination, configureManifest);

        return destination;
    }

    /// <summary>Creates a plugin folder with hand-written manifest content and no assembly.</summary>
    public string AddRawFolder(string folderName, string manifestContent)
    {
        var destination = Path.Combine(Root, folderName);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "plugin.json"), manifestContent);
        return destination;
    }

    public string ManifestPath(string folderName) => Path.Combine(Root, folderName, "plugin.json");

    public JsonObject ReadManifest(string folderName) =>
        JsonNode.Parse(File.ReadAllText(ManifestPath(folderName)))!.AsObject();

    public void EditManifest(string folderPath, Action<JsonObject> configure)
    {
        var path = Path.Combine(folderPath, "plugin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        configure(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Removes just the manifest, which makes scanning skip the folder.
    /// </summary>
    /// <remarks>
    /// The way to make a *loaded* plugin vanish in a test: Windows locks the assembly file
    /// while its load context is alive, so deleting the whole folder fails with a sharing
    /// violation. Removing the manifest exercises the same "no longer seen by a scan" path.
    /// </remarks>
    public void DeleteManifest(string folderName) => File.Delete(ManifestPath(folderName));

    public void DeleteFolder(string folderName)
    {
        var path = Path.Combine(Root, folderName);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    public void Dispose()
    {
        // Best effort: a plugin whose context is still alive keeps its dll file locked.
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string LocateRepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("PLUGMAN_REPO_ROOT") is { Length: > 0 } fromEnvironment)
            return fromEnvironment;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Plugman.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

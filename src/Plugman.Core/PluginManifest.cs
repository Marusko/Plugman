using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Plugman.Core;

/// <summary>
/// The on-disk <c>plugin.json</c> that sits next to a plugin's entry assembly.
/// </summary>
/// <remarks>
/// The manifest is the source of truth for the enabled state on startup, and it is read
/// <em>before</em> any assembly is loaded, so the manager knows which shared-assembly allow
/// list to apply and whether this host can support the plugin at all.
/// </remarks>
public sealed class PluginManifest
{
    /// <summary>Conventional file name of a manifest.</summary>
    public const string FileName = "plugin.json";

    /// <summary>Stable unique id. Must match <see cref="Contracts.PluginMetadata.Id"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>File name of the plugin's entry assembly, relative to the plugin folder.</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>
    /// Full name of the type implementing <see cref="Contracts.IPlugin"/>. Optional: when
    /// omitted the manager looks for exactly one implementation in the entry assembly.
    /// </summary>
    public string? EntryType { get; set; }

    /// <summary>Persisted enabled state, rewritten by EnableAsync/DisableAsync.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Declared UI capabilities, e.g. <c>["wpf"]</c>. Empty or omitted for a pure logic plugin.</summary>
    public string[]? UiCapabilities { get; set; }

    /// <summary>Ids of plugins that must load first. Mirrors <see cref="Contracts.PluginMetadata.Dependencies"/>.</summary>
    public string[]? Dependencies { get; set; }

    // Optional descriptive fields. They let a host show something meaningful for a plugin that
    // is discovered but not loaded (or that failed to load), without activating it first.
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Normalized, lower-cased, de-duplicated capability list.</summary>
    public IReadOnlyList<string> NormalizedUiCapabilities =>
        UiCapabilities is null || UiCapabilities.Length == 0
            ? []
            : UiCapabilities
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// Reads and validates a manifest.
    /// </summary>
    /// <exception cref="PluginManifestException">The file is missing, malformed or incomplete.</exception>
    public static PluginManifest Load(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new PluginManifestException(manifestPath, $"No {FileName} found at {manifestPath}.");

        PluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize<PluginManifest>(stream, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException(manifestPath, $"Malformed JSON in {FileName}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PluginManifestException(manifestPath, $"Could not read {FileName}: {ex.Message}", ex);
        }

        if (manifest is null)
            throw new PluginManifestException(manifestPath, $"{FileName} is empty.");

        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new PluginManifestException(manifestPath, $"{FileName} is missing the required 'id' property.");

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new PluginManifestException(manifestPath, $"{FileName} is missing the required 'entryAssembly' property.");

        var unknown = manifest.NormalizedUiCapabilities.Where(c => !PluginUiCapability.IsKnown(c)).ToArray();
        if (unknown.Length > 0)
        {
            throw new PluginManifestException(
                manifestPath,
                $"{FileName} declares unknown uiCapabilities [{string.Join(", ", unknown)}]. " +
                $"Known values are [{string.Join(", ", PluginUiCapability.Known)}].");
        }

        return manifest;
    }

    /// <summary>
    /// Rewrites only the <c>enabled</c> flag, preserving every other property in the file
    /// (including properties Plugman knows nothing about) and the author's own key order.
    /// </summary>
    public static void PersistEnabled(string manifestPath, bool enabled)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(
                File.ReadAllText(manifestPath),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
        }
        catch (Exception ex)
        {
            throw new PluginManifestException(manifestPath, $"Could not rewrite {FileName}: {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
            throw new PluginManifestException(manifestPath, $"{FileName} does not contain a JSON object.");

        obj["enabled"] = enabled;

        // Write via a temp file so a crash mid-write cannot leave a truncated manifest behind,
        // which would make the plugin undiscoverable on the next start.
        var temp = manifestPath + ".tmp";
        File.WriteAllText(temp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, manifestPath, overwrite: true);
    }
}

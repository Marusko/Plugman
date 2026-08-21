using System.Reflection;
using Plugman.Contracts;

namespace Plugman.Core;

/// <summary>
/// The manager's mutable state for one plugin. Never handed to the host — hosts see the
/// immutable <see cref="PluginDescriptor"/> snapshot instead.
/// </summary>
internal sealed class PluginRegistration(PluginManifest manifest, string folderPath)
{
    public PluginManifest Manifest { get; set; } = manifest;

    public string FolderPath { get; set; } = folderPath;

    public string ManifestPath => Path.Combine(FolderPath, PluginManifest.FileName);

    public string Id => Manifest.Id;

    public bool IsEnabled => Manifest.Enabled;

    // --- live plugin state; all of it must be dropped before the context can unload ---

    public PluginLoadContext? LoadContext { get; set; }

    public Assembly? Assembly { get; set; }

    public IPlugin? Instance { get; set; }

    public PluginContext? Context { get; set; }

    /// <summary>
    /// Survives unloading, so <see cref="PluginManager.WaitForUnloadAsync"/> can prove the
    /// context was actually collected.
    /// </summary>
    public WeakReference? UnloadProbe { get; set; }

    public PluginLoadError? LoadError { get; set; }

    public bool IsLoaded => Instance is not null;

    /// <summary>
    /// Metadata from the live instance when loaded, otherwise synthesized from the manifest so
    /// a host can list and describe plugins it has never activated.
    /// </summary>
    public PluginMetadata Metadata =>
        _metadata ??= new PluginMetadata(
            Manifest.Id,
            Manifest.Name ?? Manifest.Id,
            Manifest.Version ?? "0.0.0",
            Manifest.Description ?? string.Empty,
            Manifest.Author ?? string.Empty,
            Manifest.Dependencies);

    private PluginMetadata? _metadata;

    /// <summary>
    /// Replaces the synthesized metadata with the plugin's own.
    /// </summary>
    /// <remarks>
    /// Every string is deep-copied. <see cref="PluginMetadata"/> itself is a shared-assembly
    /// type, but the string literals a plugin puts in it are allocated in that plugin's
    /// collectible loader allocator; holding one keeps the whole load context alive and the
    /// unload silently never completes. Copying costs nothing and removes the trap.
    /// </remarks>
    /// <remarks>
    /// The id always comes from the manifest, never from the instance: the manifest id is the
    /// key this plugin is registered under, so letting a plugin report a different id would
    /// hand the host a descriptor whose <c>Id</c> no operation accepts.
    /// </remarks>
    public void AdoptInstanceMetadata(PluginMetadata metadata) =>
        _metadata = new PluginMetadata(
            Copy(Manifest.Id),
            Copy(metadata.Name),
            Copy(metadata.Version),
            Copy(metadata.Description),
            Copy(metadata.Author),
            metadata.Dependencies?.Select(Copy).ToArray());

    private static string Copy(string? value) => value is null ? string.Empty : new string(value.AsSpan());

    /// <summary>Resets metadata to the manifest-derived form (used after unload).</summary>
    public void ResetMetadata() => _metadata = null;

    /// <summary>Effective dependency ids: the manifest's list, falling back to loaded metadata.</summary>
    public IReadOnlyList<string> Dependencies =>
        Manifest.Dependencies ?? Metadata.Dependencies ?? [];

    public PluginDescriptor ToDescriptor() => new(
        Metadata,
        FolderPath,
        IsEnabled,
        IsLoaded,
        LoadError)
    {
        UiCapabilities = Manifest.NormalizedUiCapabilities
    };
}

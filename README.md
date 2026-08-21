# Plugman

A .NET 10 plugin system that embeds in any kind of host — console, WPF, Blazor Server,
ASP.NET Core, worker service — and lets plugins optionally bring their own UI in whatever
framework the host happens to use.

Plugins live in a folder, one subfolder each, with a compiled `.dll`, any private
dependencies, and a `plugin.json` manifest. The host gets typed method calls; there is no
host-side reflection anywhere.

```csharp
await using var manager = new PluginManager("./plugins", hostServices, loggerFactory);

await manager.ScanAsync();
await manager.LoadEnabledAsync();

foreach (var plugin in manager.GetPlugins<ICommandPlugin>())
    Console.WriteLine(await plugin.ExecuteAsync("time", args, ct));
```

## Guides

- [Building and installing a plugin](docs/plugin-authoring.md) — project setup, manifest
  reference, capabilities, private dependencies, deployment, troubleshooting.
- [Hosting Plugman and using plugins](docs/host-integration.md) — wiring the manager, consuming
  capabilities, enable/disable rules, per-framework recipes, trust boundary, operations.

## Why the packages are split

A single package would force a WPF reference onto a console host, or a Blazor reference onto a
WPF host. So the UI story lives in optional add-ons that only the hosts and plugins that need
them ever reference:

| Package | TFM | Contains | Referenced by |
| --- | --- | --- | --- |
| `Plugman.Contracts` | `net10.0` | `IPlugin`, `PluginMetadata`, `IPluginContext`, `ICommandPlugin`, `IConsoleUiPlugin` | everyone |
| `Plugman.Core` | `net10.0` | `PluginManager`, `PluginLoadContext`, scanning, persistence | every host |
| `Plugman.Contracts.Wpf` | `net10.0-windows` | `IWpfUiPlugin` | WPF hosts and WPF plugins |
| `Plugman.Contracts.Blazor` | `net10.0` | `IBlazorUiPlugin` | Blazor hosts and Blazor plugins |

`Plugman.Core` never references either UI package. It hands back `IPlugin` and lets the host
ask "does this also implement `IWpfUiPlugin`?" through `GetPlugin<T>` / `GetPlugins<T>`. A test
(`CorePurityTests`) walks `Plugman.Core`'s referenced assemblies and fails if a WPF, Blazor or
ASP.NET Core reference ever sneaks in.

A pure logic plugin references only `Plugman.Contracts`. A plugin that wants a WPF panel also
references `Plugman.Contracts.Wpf` and implements `IWpfUiPlugin` alongside `IPlugin`. Nothing
stops one plugin from implementing both UI capabilities — that is the plugin author's choice,
not something the core imposes.

## Layout

```
src/
  Plugman.Contracts          contracts, no UI dependency
  Plugman.Core               manager + loader
  Plugman.Contracts.Wpf      optional WPF capability
  Plugman.Contracts.Blazor   optional Blazor capability
samples/
  Plugman.Sample.ConsoleHost       REPL + scripted --demo
  Plugman.Sample.WpfHost           plugin list + a TabControl of plugin views
  Plugman.Sample.BlazorServerHost  plugin table + DynamicComponent panels
  Plugman.Sample.Plugins/
    SampleLogicPlugin        IPlugin + ICommandPlugin + IConsoleUiPlugin
    SampleWpfPlugin          IPlugin + IWpfUiPlugin (XAML view + view model)
    SampleBlazorPlugin       IPlugin + IBlazorUiPlugin (.razor component)
tests/
  Plugman.Core.Tests         scanning, loading, isolation, persistence, unloading
  Plugman.Wpf.Tests          real WPF views on an STA thread
  Plugman.Blazor.Tests       real component rendering through DynamicComponent
  Fixtures/                  small plugins built into artifacts/test-plugins
plugins/                     runtime folder the samples share (build output, not source)
```

Building a sample plugin deploys it into `/plugins/<name>/`; building a test fixture deploys it
into `/artifacts/test-plugins/<name>/`. Both go through one shared MSBuild target in
`Directory.Build.targets`.

```bash
dotnet build && dotnet test
```

```bash
dotnet run --project samples/Plugman.Sample.ConsoleHost -- --demo
```

The console host also has a REPL (run it without `--demo`): `list`, `scan`, `load`, `unload`,
`enable`, `disable`, `run <id> <command>`, `interactive <id>`. The other two:

```bash
dotnet run --project samples/Plugman.Sample.BlazorServerHost
```

```bash
dotnet run --project samples/Plugman.Sample.WpfHost
```

All three read the same `/plugins` folder, so the same three sample plugins show up in each —
including the ones a given host has to refuse.

## The manifest

```json
{
  "id": "com.mycompany.samplewpf",
  "entryAssembly": "SampleWpfPlugin.dll",
  "entryType": "SampleWpfPlugin.SampleWpfPlugin",
  "enabled": true,
  "uiCapabilities": ["wpf"],
  "dependencies": []
}
```

- `enabled` is the persisted state. `EnableAsync`/`DisableAsync` rewrite it, preserving every
  other property in the file — including keys Plugman knows nothing about. On startup the file
  is the source of truth. If the rewrite fails (the file was deleted, the folder is read-only)
  the plugin is still unloaded, and the failure is reported on its descriptor rather than
  swallowed.
- `entryType` is optional; with it omitted the manager looks for exactly one `IPlugin`
  implementation in the entry assembly.
- `uiCapabilities` is an array, so a plugin can advertise more than one. Omit it for a pure
  logic plugin.
- Optional `name`, `version`, `description`, `author` let a host display a plugin it has
  discovered but never loaded.

The manager reads the manifest *before* it loads anything. That is what lets it pick the right
shared-assembly allow list, and lets it reject a plugin this host could never render without
touching the assembly at all:

```
[HostCapability] Plugin 'com.plugman.sample.blazor' declares UI capability [blazor] which this
host does not support. This host supports: none (logic plugins only). The plugin was not
loaded; no assembly was touched.
```

## Isolation

Each plugin loads into its own collectible `AssemblyLoadContext`, with an
`AssemblyDependencyResolver` pointed at the plugin's own dll so private dependencies resolve
from the plugin folder. Two plugins carrying different versions of the same private dependency
coexist without a `FileLoadException` — there is a test for exactly that.

The critical part is `PluginLoadContext.Load`, which checks a shared-assembly allow list
**before** the resolver:

1. On the allow list → return `null`, so the runtime falls through to the default context and
   the host's copy wins.
2. Resolvable from the plugin's `.deps.json` → load it from the plugin folder.
3. Otherwise `null` — framework assemblies fall through as well.

The allow list is built from the manifest:

| Declared | Shared with the host |
| --- | --- |
| always | `Plugman.Contracts`, `Microsoft.Extensions.Logging.Abstractions` |
| `"wpf"` | `Plugman.Contracts.Wpf`, `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml` |
| `"blazor"` | `Plugman.Contracts.Blazor`, `Microsoft.AspNetCore.Components`, `Microsoft.AspNetCore.Components.Web` |

Add your own host contract assemblies through `PluginManagerOptions.AdditionalSharedAssemblies`
if plugins resolve host services out of `IPluginContext.Services`.

Getting this wrong is the single most common failure mode in a system like this. If
`FrameworkElement` or `IComponent` loads twice — once in the host, once in the plugin's context
— every cast throws `InvalidCastException` even though the type names match, because type
identity is (name, assembly, load context). Three things keep it right, and all three are
tested:

- the allow list is consulted ahead of the resolver;
- plugin projects reference the contracts with `Private="false" ExcludeAssets="runtime"`, so no
  contract dll is ever copied into a plugin folder;
- if it still happens, the manager says so in plain language instead of throwing a
  `TypeLoadException` from somewhere inside activation.

Plugin calls run inside `alc.EnterContextualReflection()`. Without it a WPF plugin's
`InitializeComponent` cannot resolve its own compiled XAML, because the pack URI resolves the
assembly by name against the default context.

## Writing a plugin

```csharp
public sealed class MyPlugin : ICommandPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "com.mycompany.myplugin",
        Name: "My Plugin",
        Version: "1.0.0",
        Description: "Does a thing.",
        Author: "me");

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) { ... }
    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct) { ... }
    public Task ShutdownAsync(CancellationToken ct) { ... }
}
```

The project needs three things:

```xml
<EnableDynamicLoading>true</EnableDynamicLoading>

<ProjectReference Include="...\Plugman.Contracts.csproj" Private="false" ExcludeAssets="runtime" />
<None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
```

`EnableDynamicLoading` is what makes a class library loadable: it emits the `.deps.json` the
dependency resolver reads and copies private dependencies next to the dll.

## Host integration

All three hosts share the same startup: construct the manager, `ScanAsync`, `LoadEnabledAsync`.
Everything after that is just asking for a capability.

**Console** — `GetPlugins<ICommandPlugin>()` for dispatch, `IConsoleUiPlugin` for a plugin that
wants the terminal for a while.

**WPF** — iterate the loaded plugins that declare `"wpf"`, call `CreateView()` through the
manager, and drop the returned `FrameworkElement` into a `TabItem`. No DataTemplates, no
ViewModel-first plumbing, no per-plugin host code. When disabling, remove the view from the
visual tree *before* calling `DisableAsync`.

One trap worth knowing, because it looks exactly like a plugin bug: a `TabControl` populated at
runtime keeps `SelectedIndex` at `-1`, and an unselected tab never realizes its content. The tab
header appears, the plugin's view is sitting in it — with `IsVisible` false and a zero size —
and the panel renders blank. Select the tab explicitly after adding it (and after removing the
selected one). `TabHostingTests` covers this; a `ContentControl`-based test cannot, because a
`ContentControl` has no notion of selection.

**Blazor Server** — render `GetPlugins<IBlazorUiPlugin>()` with `DynamicComponent`:

```razor
<DynamicComponent Type="panel.ComponentType" Parameters="panel.Parameters" />
```

Keep plugin components *inside* an existing render-mode boundary (a globally interactive
`<Routes @rendermode="InteractiveServer" />`) rather than making a plugin component the
boundary. A boundary component's type is serialized by assembly name for the circuit to
re-resolve, which cannot work for a type in a collectible load context.

Anything that runs plugin code should go through the manager boundary so a misbehaving plugin
cannot take the host down:

```csharp
var view = manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(id, p => p.CreateView(context));
if (!view.Success)
    ShowError(view.Error!.Message);   // recorded on the descriptor too
```

## What actually unloads

`DisableAsync` shuts the plugin down, drops every reference Plugman holds, and unloads the load
context. Whether the memory then comes back depends on what the host — and the UI framework —
did with the plugin first. This was measured, not assumed; each row is a test.

| Situation | Collected? |
| --- | --- |
| Logic plugin, any host | yes |
| WPF plugin that was never rendered | yes |
| WPF plugin whose view is built in code | yes, once the view leaves the visual tree |
| WPF plugin whose view comes from compiled XAML | **no** |
| Blazor plugin that was loaded and listed but never rendered | yes |
| Blazor plugin whose component has been rendered | **no** |

The two "no" rows are framework behaviour, not a Plugman bug, and there is no supported way
around them:

- WPF pins any assembly it has loaded compiled XAML (BAML) out of, for the life of the process.
  The pin follows the assembly identity, so even a later, fresh load context for the same
  plugin assembly stays alive.
- Blazor caches per-component-type activation metadata in a process-wide static the first time
  a component type is rendered. Reading `ComponentType` is free; rendering it is what pins.

In both cases the plugin is still stopped, disabled and gone from the UI — only the memory is
not reclaimed. If unloading matters more to you than authoring convenience, build WPF plugin
views in code (see `tests/Fixtures/WpfCodeViewPlugin`) and accept that Blazor panels pin their
assembly once shown. `WpfUnloadTests` and `BlazorUnloadTests` assert the current behaviour in
both directions, so if a future .NET release stops pinning, those tests fail and this section
needs deleting.

Two things a *host* can get wrong, both of which silently defeat an otherwise clean unload:

- keeping a plugin instance in a field or a long-lived local. In a debug build the JIT keeps a
  local alive for the whole method, so `var p = manager.GetPlugin<T>(id)` at the top of a method
  pins the plugin until that method returns. Work from `DiscoveredPlugins` descriptors, which
  are plain immutable data, and fetch instances only for the call you are making.
- caching a `Type` obtained from a plugin (`IBlazorUiPlugin.ComponentType`) or a
  `FrameworkElement` it built. Both root the load context exactly like an instance does.

`PluginManager.WaitForUnloadAsync(id, timeout)` forces collection in a loop and reports whether
the context actually died. It is a development and test tool — do not put it in a hot path.

## Failures

Nothing about a broken plugin escapes as an exception into the host. Everything lands on
`PluginDescriptor.LoadError` as data:

| Stage | Cause |
| --- | --- |
| `Manifest` | missing/malformed `plugin.json`, missing `id`, unknown `uiCapabilities` value, failed persist |
| `HostCapability` | the plugin declares a UI capability this host cannot support |
| `Dependency` | a required plugin is missing, failed, or the graph has a cycle |
| `Assembly` | the entry assembly is missing or will not load |
| `Activation` | entry type missing, not an `IPlugin`, or ambiguous |
| `Initialize` | `InitializeAsync` threw — the load is rolled back |
| `Capability` | a call made through `TryInvoke`/`InvokeAsync` threw |
| `Shutdown` | `ShutdownAsync` threw — the unload continues anyway |

`PluginLoadError` deliberately stores strings only, never the `Exception`. Holding the
exception would root the plugin's load context through its stack trace, so the failure report
itself would keep the failing plugin alive forever.

A malformed manifest never aborts a scan: the plugin appears in `DiscoveredPlugins` under its
folder name with the error attached, and every other plugin loads normally.

A scan also reconciles removals. A plugin that has disappeared from disk and is *not* loaded is
dropped from the registry (`PluginState.Removed`). One that is still loaded is kept and flagged
instead — deleting a file does not un-run the code, and silently unloading on a transient
filesystem problem would be a worse failure than a stale row. It is dropped by the first scan
after it is unloaded.

## API

```csharp
public sealed class PluginManager : IAsyncDisposable
{
    PluginManager(string pluginsRootDirectory, IServiceProvider hostServices, ILoggerFactory loggerFactory);
    PluginManager(string pluginsRootDirectory, IServiceProvider hostServices, ILoggerFactory loggerFactory, PluginManagerOptions options);

    IReadOnlyList<PluginDescriptor> DiscoveredPlugins { get; }
    IReadOnlyList<string> HostUiCapabilities { get; }

    Task ScanAsync(CancellationToken ct = default);
    Task<PluginDescriptor> LoadAsync(string pluginId, CancellationToken ct = default);
    Task<IReadOnlyList<PluginDescriptor>> LoadEnabledAsync(CancellationToken ct = default);
    Task UnloadAsync(string pluginId, CancellationToken ct = default);

    Task<PluginDescriptor> EnableAsync(string pluginId, CancellationToken ct = default);
    Task<PluginDescriptor> DisableAsync(string pluginId, CancellationToken ct = default);

    T? GetPlugin<T>(string pluginId) where T : class, IPlugin;
    IEnumerable<T> GetPlugins<T>() where T : class, IPlugin;
    PluginDescriptor? GetDescriptor(string pluginId);
    IPluginContext? GetPluginContext(string pluginId);

    PluginInvocationResult<TResult> TryInvoke<TPlugin, TResult>(string pluginId, Func<TPlugin, TResult> call);
    Task<PluginInvocationResult<TResult>> InvokeAsync<TPlugin, TResult>(string pluginId, Func<TPlugin, CancellationToken, Task<TResult>> call, CancellationToken ct = default);

    Task<bool> WaitForUnloadAsync(string pluginId, TimeSpan timeout, CancellationToken ct = default);

    event EventHandler<PluginStateChangedEventArgs>? PluginStateChanged;
}
```

`PluginManagerOptions` covers the per-plugin data directory root, an explicit
`HostUiCapabilities` list (otherwise probed from the assemblies present in the default load
context), `AdditionalSharedAssemblies`, `AutoLoadEnabledPlugins`, and timeouts for
`InitializeAsync` and `ShutdownAsync`. Both lifecycle calls are made while the manager holds its
lock, so the timeouts are what stop a plugin that never returns from deadlocking every later
operation: the token is cancelled, and a plugin that ignores it is abandoned rather than waited
on. The host keeps running; that plugin's load context never collects.

State-change handlers run after the manager's internal lock is released, so a handler that
calls back into the manager — a UI refreshing itself, for instance — cannot deadlock.

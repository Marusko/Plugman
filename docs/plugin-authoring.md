# Building and installing a Plugman plugin

Everything a plugin author needs: project setup, the manifest, the capability interfaces, how
to ship the folder, and what to do when something does not load.

- [1. What a plugin actually is](#1-what-a-plugin-actually-is)
- [2. Quick start: a logic plugin](#2-quick-start-a-logic-plugin)
- [3. The project file, line by line](#3-the-project-file-line-by-line)
- [4. Manifest reference](#4-manifest-reference)
- [5. Capabilities](#5-capabilities)
- [6. Lifecycle rules](#6-lifecycle-rules)
- [7. The plugin context](#7-the-plugin-context)
- [8. Private dependencies](#8-private-dependencies)
- [9. Depending on another plugin](#9-depending-on-another-plugin)
- [10. Installing and updating](#10-installing-and-updating)
- [11. Testing your plugin](#11-testing-your-plugin)
- [12. Troubleshooting](#12-troubleshooting)
- [13. Release checklist](#13-release-checklist)

---

## 1. What a plugin actually is

One folder under the host's plugins root:

```
plugins/
  MyPlugin/
    MyPlugin.dll             the entry assembly
    MyPlugin.deps.json       generated; the loader reads it to find private dependencies
    SomeLibrary.dll          private dependencies, your copy, your version
    plugin.json              the manifest
```

The host loads that folder into its own collectible `AssemblyLoadContext`, activates the one
type named in the manifest, and calls it through interfaces both sides share. Everything else
in the folder is yours.

Two rules follow from that, and most plugin problems are one of them:

- **Contract assemblies must come from the host, never from your folder.** They are the types
  that cross the boundary. Your project references them at compile time only.
- **Everything else should be in your folder.** That is what lets your plugin carry a different
  version of a library than the host or another plugin.

## 2. Quick start: a logic plugin

A plugin with no UI, loadable in any host.

```bash
dotnet new classlib -o MyPlugin -f net10.0
```

`MyPlugin.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Plugman.Contracts\Plugman.Contracts.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`MyPlugin.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Plugman.Contracts;

namespace MyPlugin;

public sealed class MyPlugin : ICommandPlugin
{
    private IPluginContext? _context;

    public PluginMetadata Metadata { get; } = new(
        Id: "com.mycompany.myplugin",
        Name: "My Plugin",
        Version: "1.0.0",
        Description: "Does one useful thing.",
        Author: "My Company");

    public IReadOnlyList<string> Commands => ["greet"];

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        context.Logger.LogInformation("My plugin is up.");
        return Task.CompletedTask;
    }

    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct) =>
        Task.FromResult(command switch
        {
            "greet" => $"Hello, {(args.TryGetValue("name", out var name) ? name : "world")}.",
            _ => $"Unknown command '{command}'."
        });

    public Task ShutdownAsync(CancellationToken ct)
    {
        _context = null;
        return Task.CompletedTask;
    }
}
```

`plugin.json`:

```json
{
  "id": "com.mycompany.myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "description": "Does one useful thing.",
  "author": "My Company",
  "entryAssembly": "MyPlugin.dll",
  "entryType": "MyPlugin.MyPlugin",
  "enabled": true,
  "uiCapabilities": []
}
```

Build, copy the output folder into the host's `plugins/` directory, and rescan. In the console
sample:

```bash
dotnet run --project samples/Plugman.Sample.ConsoleHost
```

```
> scan
> run com.mycompany.myplugin greet name=you
```

### Deploying automatically at build time

The repository's `Directory.Build.targets` publishes any project that sets `PluginDeployRoot`
into a plugin folder. For a plugin inside this repo:

```xml
<PluginDeployRoot>$(PluginsRoot)</PluginDeployRoot>
```

Outside the repo, add a post-build copy of your own, or use `dotnet publish` (see
[§10](#10-installing-and-updating)).

## 3. The project file, line by line

| Setting | Why it is there |
| --- | --- |
| `<EnableDynamicLoading>true</EnableDynamicLoading>` | Turns a class library into a loadable plugin: emits `.deps.json` and copies private dependencies next to your dll. Without it the loader cannot resolve your dependencies and your folder is missing files. |
| `Private="false"` on contract references | Keeps `Plugman.Contracts.dll` (and the UI contract dlls) out of your output folder. |
| `ExcludeAssets="runtime"` on contract references | Also keeps them out of your `.deps.json`, so nothing ever tries to resolve them locally. |
| `<None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />` | Ships the manifest with the build output. |

**Do not** set `Private="true"` (the default) on `Plugman.Contracts`. A copy of the contracts in
your folder is the single most common cause of a plugin that "obviously implements `IPlugin`"
but is rejected — see [§12](#12-troubleshooting).

Target framework: match the host's major version (`net10.0`). A WPF plugin needs
`net10.0-windows` and `<UseWPF>true</UseWPF>`.

## 4. Manifest reference

`plugin.json` sits next to the entry assembly. The manager reads it *before* loading anything,
which is how it decides whether this host can support your plugin at all.

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Stable unique id, reverse-DNS by convention. This is the key the host addresses your plugin by, and it wins over the id in `PluginMetadata` if they disagree. Changing it later is a breaking change for anyone who has enabled/disabled it. |
| `entryAssembly` | yes | File name of your dll, relative to the plugin folder. |
| `entryType` | no | Full name of your `IPlugin` implementation. Omit it only if the assembly contains exactly one; naming it is faster and gives better errors. |
| `enabled` | no (default `true`) | Persisted state. The host rewrites this field on enable/disable and preserves everything else in the file, including keys Plugman does not know about. |
| `uiCapabilities` | no | `["wpf"]`, `["blazor"]`, or both. Omit for a logic-only plugin. |
| `dependencies` | no | Ids of plugins that must load before yours. |
| `name`, `version`, `description`, `author` | no | Shown by hosts for a plugin that is discovered but not loaded. Once loaded, your `PluginMetadata` supplies these instead. |

Notes:

- Comments and trailing commas are tolerated when reading.
- An unknown value in `uiCapabilities` is a hard error for that plugin (it is reported, the scan
  continues) — this catches typos like `"WPF "` or `"winforms"` at discovery time.
- Keep your own configuration in a separate file in the plugin folder, or in the plugin data
  directory. Extra keys in `plugin.json` survive rewrites, but the manifest is the host's file.

## 5. Capabilities

A capability is an interface you additionally implement. The host asks for it by type; nothing
else changes.

### `ICommandPlugin` — non-UI work (in `Plugman.Contracts`)

```csharp
public IReadOnlyList<string> Commands => ["import", "export"];

public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct);
```

`Commands` is optional (defaults to empty) but lets a host build help and menus.

### `IConsoleUiPlugin` — take over the terminal (in `Plugman.Contracts`)

```csharp
public string ScreenTitle => "My plugin console";

public Task RunInteractiveAsync(IPluginContext context, CancellationToken ct);
```

Lives in the core contracts because a console needs no UI framework. Return when the user exits
or `ct` is cancelled.

### `IWpfUiPlugin` — a WPF panel (needs `Plugman.Contracts.Wpf`)

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
```

```csharp
public string ViewTitle => "My panel";

public FrameworkElement CreateView(IPluginContext context)
{
    return new MyView(new MyViewModel(context));   // view + view model are yours
}
```

- Called on the UI thread. It may be called more than once, so do not assume a single instance.
- You own your MVVM entirely. The host just hosts the element you return.
- Declare `"uiCapabilities": ["wpf"]`.
- **Compiled XAML pins your assembly.** If your view uses `InitializeComponent()`, WPF keeps
  your assembly alive for the life of the process and your plugin can never be fully unloaded.
  Building views in code avoids this. Either is supported; pick based on whether the host needs
  to reclaim memory on disable. See [`tests/Fixtures/WpfCodeViewPlugin`](../tests/Fixtures/WpfCodeViewPlugin)
  for a code-built view.

### `IBlazorUiPlugin` — a Razor component (needs `Plugman.Contracts.Blazor`)

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <ScopedCssEnabled>false</ScopedCssEnabled>
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>
```

```csharp
public Type ComponentType => typeof(MyPanel);

public IReadOnlyDictionary<string, object?>? DefaultParameters { get; } =
    new Dictionary<string, object?> { ["Title"] = "My panel" };
```

- Declare `"uiCapabilities": ["blazor"]`.
- Do **not** put `@rendermode` on your component. It must render inside the host's existing
  interactivity boundary; a render-mode boundary component's type is serialized by assembly name
  for the circuit to re-resolve, which cannot work for a collectible load context.
- CSS isolation and static web assets do not work: your folder is not served by the host's
  static file middleware. Style inline or rely on the host's stylesheet.
- Rendering your component pins your assembly for the life of the process (Blazor caches
  activation metadata per component type). Reading `ComponentType` does not.

### Implementing more than one

Nothing stops a plugin from implementing `IWpfUiPlugin` *and* `IBlazorUiPlugin` *and*
`ICommandPlugin`. Declare both UI capabilities in the manifest; a host that supports only one
will refuse to load the plugin, so a multi-host plugin is usually better split into a shared
logic assembly plus thin per-framework plugins.

## 6. Lifecycle rules

```
discover (manifest) → load assembly → activate type → InitializeAsync → …use… → ShutdownAsync → unload
```

**`Metadata`** is read immediately after activation, before `InitializeAsync`. Make it a cheap,
side-effect-free property — ideally a field initializer, as in the samples.

**`InitializeAsync`**

- Throwing here aborts the load: the manager records a `PluginLoadError` and rolls the load back
  (no half-initialized plugin is left behind).
- It runs while the manager holds its lock, so it has a timeout (30s by default). Honour the
  `CancellationToken`. If you ignore it and never return, the manager abandons the call to keep
  the host alive, your plugin does not load, and your load context can never be collected.
- Do not block the thread (`.Result`, `.Wait()`); use `await`.

**`ShutdownAsync`**

- Release everything: timers, file handles, event subscriptions, background tasks, anything you
  registered with a host service. Whatever you leave running keeps your load context alive.
- Also has a timeout (10s by default) with the same abandonment rule.
- Called before every unload, including on host shutdown.

**Threads.** Nothing guarantees which thread your lifecycle methods run on — the manager
`ConfigureAwait(false)`s throughout. UI capability methods (`CreateView`) are called on the
host's UI thread. If you need the UI thread elsewhere, capture the dispatcher inside
`CreateView`.

**Failures during a capability call** (a command, building a view) do not unload you: they are
caught at the manager boundary, recorded on your descriptor, and the host stays up.

## 7. The plugin context

```csharp
public interface IPluginContext
{
    ILogger Logger { get; }              // scoped to your plugin id
    IServiceProvider Services { get; }   // host services, read-only
    string PluginDataDirectory { get; }  // yours, created before InitializeAsync
}
```

- **Logger** — entries are attributed to `Plugman.Plugin.<your-id>`. Use it instead of
  `Console.WriteLine`; hosts route it wherever their logging goes.
- **Services** — anything you resolve here must be typed against an assembly the host shares
  with you (see [§8](#8-private-dependencies)). Ask the host owner to add their service contract
  assembly to `AdditionalSharedAssemblies`; without that, the cast fails across load contexts.
  Never `Dispose()` something you resolve — you do not own the host's container.
- **PluginDataDirectory** — a per-plugin folder for your own state. Already created. It is not
  deleted when you are disabled, so treat its contents as durable and version them yourself.

Hold the context in a field during `InitializeAsync` if you need it later; the same instance is
what the host passes back to `CreateView`/`RunInteractiveAsync`.

## 8. Private dependencies

Reference NuGet packages normally. With `EnableDynamicLoading`, they are copied next to your dll
and listed in your `.deps.json`, and the loader resolves them from your folder:

```xml
<PackageReference Include="CsvHelper" Version="33.0.1" />
```

Two plugins can use different versions of the same package with no conflict — each resolves its
own copy. That is the whole point of the per-plugin load context, and it is covered by a test.

**The exception is shared assemblies**, which always resolve from the host regardless of what is
in your folder:

| Always shared | `Plugman.Contracts`, `Microsoft.Extensions.Logging.Abstractions` |
| --- | --- |
| Shared when you declare `"wpf"` | `Plugman.Contracts.Wpf`, `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml` |
| Shared when you declare `"blazor"` | `Plugman.Contracts.Blazor`, `Microsoft.AspNetCore.Components`, `Microsoft.AspNetCore.Components.Web` |

Plus anything the host added to `AdditionalSharedAssemblies`. Framework assemblies
(`System.*`) are not in your `.deps.json` and come from the shared framework as usual.

Practical consequence: if you reference a *newer* version of a shared assembly than the host has,
you get the host's version at runtime. Compile against the version the host ships.

**Native libraries** work too — `EnableDynamicLoading` copies the runtime-specific natives into
your folder and the load context resolves them from there.

## 9. Depending on another plugin

```json
"dependencies": ["com.mycompany.core"]
```

The manager loads dependencies first, detects cycles, and fails your plugin with a
`Dependency`-stage error if a dependency is missing, disabled, or failed.

What it does **not** do is give you a typed reference to that plugin: separate load contexts
mean you cannot cast its instance to a type from its assembly. To share code between plugins,
put the shared types in an assembly the host shares (`AdditionalSharedAssemblies`), or
communicate through a host service. There are no version constraints on dependencies — the id
is matched, not the version.

## 10. Installing and updating

### Producing the folder

`dotnet build` output already is a plugin folder when `EnableDynamicLoading` is set. For
distribution, prefer:

```bash
dotnet publish MyPlugin.csproj -c Release -o out/MyPlugin
```

Then check the folder:

- ✅ `MyPlugin.dll`, `MyPlugin.deps.json`, `plugin.json`, your private dependencies
- ❌ `Plugman.Contracts.dll`, `Plugman.Contracts.Wpf.dll`, `Plugman.Contracts.Blazor.dll`,
  `Microsoft.Extensions.Logging.Abstractions.dll` — if any of these are present, your
  `Private`/`ExcludeAssets` settings are wrong
- ❌ a `.exe` apphost — harmless, but a sign you published as an app rather than a library

`.pdb` files are fine and make stack traces in `PluginLoadError.Details` readable.

### Installing

Copy the folder into the host's plugins root, then have the host `ScanAsync()`. No restart is
required — dropping a folder in and rescanning is a supported, tested path. If the host has no
rescan button, restarting it picks the plugin up.

Whether it then loads depends on `enabled` in your manifest: ship `true` if you want it active
on arrival, `false` if the operator should opt in.

### Updating

The dll of a **loaded** plugin is locked on Windows. To update:

1. Have the host disable (or unload) the plugin.
2. Replace the folder contents.
3. Enable it again.

If the plugin has ever rendered a WPF XAML view or a Blazor component, its assembly stays pinned
even after unloading, and the file may remain locked until the process restarts. For hosts that
update plugins in place, plan on a restart, or build WPF views in code.

### Uninstalling

Disable first, then delete the folder, then rescan. Deleting the folder of a loaded plugin does
not stop it: the host keeps it listed (flagged as "folder no longer exists") until it is
unloaded, because deleting a file cannot un-run code.

## 11. Testing your plugin

Test the plugin class directly — it is an ordinary class:

```csharp
var plugin = new MyPlugin();
await plugin.InitializeAsync(new FakeContext(), CancellationToken.None);

Assert.Equal("Hello, you.", await plugin.ExecuteAsync("greet", new Dictionary<string, string> { ["name"] = "you" }, default));
```

Then test it *as a plugin*, which is what catches packaging mistakes — a contract dll in the
folder, a wrong `entryType`, a missing `deps.json`:

```csharp
await using var manager = new PluginManager(pluginsRoot, services, loggerFactory);
await manager.ScanAsync();

var descriptor = await manager.LoadAsync("com.mycompany.myplugin");
Assert.True(descriptor.IsLoaded, descriptor.LoadError?.Message);
```

The fixtures under [`tests/Fixtures`](../tests/Fixtures) are minimal working examples of every
shape: logic, hanging, throwing, conflicting dependencies, WPF code-built view. The helpers in
[`tests/Shared`](../tests/Shared) show how to build a throwaway plugins folder for a test.

For UI plugins, drive the real thing: a WPF view needs an STA thread and a live `TabControl`
(see `TabHostingTests`); a Razor component can be rendered headlessly with `HtmlRenderer` and
`DynamicComponent` (see `BlazorUiPluginTests`).

## 12. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Type 'X' implements Plugman.Contracts.IPlugin from a second copy of 'Plugman.Contracts' loaded out of the plugin folder` | The contracts dll was copied into your folder. | `Private="false" ExcludeAssets="runtime"` on the contract references; delete the stale file from the deployed folder. |
| `Entry type 'X' was not found in 'Y.dll'` | Typo, wrong namespace, or the type is not public. | `entryType` is the **full** name (`Namespace.Type`), case-sensitive. |
| `'Y.dll' contains no public type implementing IPlugin` | The class is not public, is abstract, or the manifest points at the wrong assembly. | Make it a public non-abstract class, or name it explicitly with `entryType`. |
| `declares UI capability [blazor] which this host does not support` | Right plugin, wrong host. | Load it in a host that supports the capability, or drop the capability from the manifest. |
| `Entry assembly 'X.dll' was not found` | The manifest name does not match the file, or the deploy step missed the dll. | Compare `entryAssembly` with the actual file name. |
| `FileNotFoundException` for one of your NuGet dependencies at runtime | `EnableDynamicLoading` is missing, so no `.deps.json` was emitted and the dependency was not copied. | Add `<EnableDynamicLoading>true</EnableDynamicLoading>` and redeploy. |
| WPF: tab header appears, panel is blank | The host's `TabControl` has nothing selected — not your bug. | Host-side: select the tab after adding it. See the host guide. |
| WPF: `InitializeComponent` throws `IOException`/`FileNotFoundException` for a pack URI | Your XAML is being resolved against the default load context. | Make sure the host calls you through the manager (`TryInvoke`), which enters contextual reflection. Calling `CreateView` directly on a cached instance skips it. |
| Blazor: component renders as static HTML, buttons do nothing | The host page is not interactive, or your component declares its own `@rendermode`. | Host-side: global interactivity on `<Routes>`. Plugin-side: no `@rendermode`. |
| Disabling does not free memory | Expected for a WPF XAML view or a rendered Blazor component; otherwise something still holds a reference. | See the unload matrix in the [README](../README.md). |
| Your plugin loads but nothing happens on shutdown | `ShutdownAsync` timed out or threw. | Check `PluginDescriptor.LoadError` (stage `Shutdown`) and the host log. |

`PluginLoadError.Details` carries the full exception chain and stack trace — ask the host
operator for it before guessing.

## 13. Release checklist

- [ ] `EnableDynamicLoading` is on; the built folder contains a `.deps.json`.
- [ ] No contract assemblies in the folder.
- [ ] `plugin.json` ships, `id` matches `PluginMetadata.Id`, `entryType` is the full type name.
- [ ] `uiCapabilities` matches the interfaces you actually implement.
- [ ] `Metadata` is cheap and side-effect free.
- [ ] `InitializeAsync` honours its `CancellationToken` and does not block.
- [ ] `ShutdownAsync` releases timers, handles, subscriptions and background tasks.
- [ ] Version bumped in both `PluginMetadata` and `plugin.json`.
- [ ] Loaded once through a real `PluginManager`, not just unit-tested in isolation.

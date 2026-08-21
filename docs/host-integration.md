# Hosting Plugman and using plugins

Everything a host author needs: wiring the manager, consuming capabilities, the lifecycle,
per-framework recipes, and the rules that decide whether a disable actually frees memory.

- [1. Startup, in three lines](#1-startup-in-three-lines)
- [2. Dependency injection](#2-dependency-injection)
- [3. Using plugins](#3-using-plugins)
- [4. Calling plugin code safely](#4-calling-plugin-code-safely)
- [5. Descriptors, state and events](#5-descriptors-state-and-events)
- [6. Enable, disable, unload](#6-enable-disable-unload)
- [7. Options reference](#7-options-reference)
- [8. Host recipes](#8-host-recipes)
- [9. Errors and what to show](#9-errors-and-what-to-show)
- [10. Threading](#10-threading)
- [11. Trust and security](#11-trust-and-security)
- [12. Operating a plugin host](#12-operating-a-plugin-host)
- [13. Do and do not](#13-do-and-do-not)

---

## 1. Startup, in three lines

Identical in every host — console, WPF, Blazor Server, ASP.NET Core, worker service:

```csharp
await using var manager = new PluginManager(pluginsRoot, hostServices, loggerFactory);

await manager.ScanAsync();          // read plugin.json files, discover what is there
await manager.LoadEnabledAsync();   // load everything whose manifest says enabled
```

| Constructor argument | What it is |
| --- | --- |
| `pluginsRootDirectory` | Folder containing one subfolder per plugin. Created if missing. Subfolders starting with `.` or `_` are skipped (the default plugin-data root lives at `<root>/.data`). |
| `hostServices` | What plugins see through `IPluginContext.Services`. See [§11](#11-trust-and-security) before handing over your whole container. |
| `loggerFactory` | Used for the manager's own log and for each plugin's logger (`Plugman.Plugin.<id>`). |

A fourth overload takes `PluginManagerOptions` ([§7](#7-options-reference)).

`DisposeAsync` shuts down and unloads every loaded plugin, so `await using` (or an explicit
dispose on host shutdown) is the whole cleanup story.

## 2. Dependency injection

```csharp
builder.Services.AddSingleton(serviceProvider => new PluginManager(
    pluginsRoot,
    serviceProvider,
    serviceProvider.GetRequiredService<ILoggerFactory>()));
```

Passing the provider to itself is intentional: plugins resolve host services from the same
container. Scan after the container is built:

```csharp
var app = builder.Build();

var manager = app.Services.GetRequiredService<PluginManager>();
await manager.ScanAsync();
await manager.LoadEnabledAsync();

app.Lifetime.ApplicationStopping.Register(() => manager.DisposeAsync().AsTask().GetAwaiter().GetResult());
```

In a generic host, an `IHostedService` is tidier — see [§8](#8-host-recipes).

The manager is thread-safe and meant to be a singleton. One manager per plugins root.

## 3. Using plugins

A capability is just an interface. Ask for it:

```csharp
foreach (var plugin in manager.GetPlugins<ICommandPlugin>())
    Console.WriteLine(string.Join(", ", plugin.Commands));

var one = manager.GetPlugin<IWpfUiPlugin>("com.mycompany.panel");
```

`T` can be `IPlugin`, a logic capability (`ICommandPlugin`, `IConsoleUiPlugin`), a UI capability
(`IWpfUiPlugin`, `IBlazorUiPlugin`), or **your own interface** — if you define a capability in an
assembly you add to `AdditionalSharedAssemblies`, `GetPlugins<TYourCapability>()` works exactly
the same way. Plugman.Core has no idea which is which.

Both methods return only *loaded* plugins, and return live references into collectible load
contexts. Fetch them for the call you are making; do not park them in a field
([§6](#6-enable-disable-unload)).

To list plugins for a UI, use descriptors instead — they are immutable data and safe to keep:

```csharp
foreach (var descriptor in manager.DiscoveredPlugins)
    rows.Add(new Row(descriptor.Id, descriptor.Metadata.Name, descriptor.IsEnabled, descriptor.IsLoaded));
```

## 4. Calling plugin code safely

Anything that runs plugin code can throw, hang, or need contextual reflection. Route it through
the manager boundary rather than calling the instance directly:

```csharp
// async capability call
var result = await manager.InvokeAsync<ICommandPlugin, string>(
    pluginId,
    (plugin, ct) => plugin.ExecuteAsync("import", args, ct));

if (result.Success)
    Show(result.Value);
else
    ShowError(result.Error!.Message);

// synchronous call — building a UI, reading a property
var view = manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(pluginId, p => p.CreateView(context));
```

What you get for free:

- **try/catch** — a throwing plugin returns `Success == false` instead of unwinding into your
  event handler or request pipeline.
- **The failure is recorded** on `PluginDescriptor.LoadError` (stage `Capability`) and a
  `Failed` state-change event is raised, so your UI can surface it without extra plumbing.
- **Logging** with the plugin id attached.
- **Contextual reflection** — the plugin's load context is current for the duration, which is
  what lets a WPF plugin's `InitializeComponent()` find its own compiled XAML. Calling
  `CreateView` directly on an instance you cached skips this and can fail with a pack-URI error.

If a capability method needs an `IPluginContext` (`CreateView`, `RunInteractiveAsync`), get the
plugin's own:

```csharp
var context = manager.GetPluginContext(pluginId);   // null when not loaded
```

Direct calls on `GetPlugin<T>()` results are fine for trivial, non-throwing property reads, but
there is no reason to skip `TryInvoke`.

## 5. Descriptors, state and events

`PluginDescriptor` is an immutable snapshot — safe to bind a UI to, safe to keep after the
plugin is unloaded, because it holds no types from the plugin's context:

| Member | Meaning |
| --- | --- |
| `Id`, `Metadata` | The plugin's own metadata once loaded; manifest values before that. |
| `FolderPath` | Where it lives. |
| `IsEnabled` | Persisted state as last written to `plugin.json`. |
| `IsLoaded` | Whether an instance is live right now. |
| `UiCapabilities` | Declared capabilities, e.g. `["wpf"]`. |
| `LoadError` / `HasError` | Last failure, as plain data. |

Subscribe for changes:

```csharp
manager.PluginStateChanged += (_, e) => Log($"{e.PluginId} → {e.State}");
```

States: `Discovered`, `Removed`, `Loaded`, `Unloaded`, `Enabled`, `Disabled`, `Failed`.
`e.Descriptor` is the post-change snapshot.

Handlers run **after** the manager releases its internal lock, so a handler may call back into
the manager without deadlocking. A handler that throws is caught and logged; it cannot break the
operation that raised it. Events arrive on whatever thread finished the operation — marshal to
your UI thread ([§10](#10-threading)).

`ScanAsync` is safe to call at any time on a running host: new folders are discovered, folders
that disappeared are dropped (or flagged if the plugin is still loaded), and `enabled` on disk
wins. Wire it to a "Rescan" button; you do not need to restart to pick up a new plugin.

## 6. Enable, disable, unload

| Call | Persists `enabled` | Loads/unloads |
| --- | --- | --- |
| `LoadAsync` / `UnloadAsync` | no | yes |
| `EnableAsync` / `DisableAsync` | yes | yes |

Use enable/disable for user-driven state that should survive a restart; use load/unload for
transient control.

**Unloading only completes if nothing references the plugin.** The manager drops everything it
holds; the host has to do the same. Three things commonly keep a plugin alive:

1. **An instance in a field or a long-lived local.** In a debug build the JIT keeps a local alive
   for the whole method, so `var p = manager.GetPlugin<T>(id)` at the top of a method pins the
   plugin until that method returns.
2. **A `Type` obtained from the plugin** — a cached `IBlazorUiPlugin.ComponentType` roots the
   context exactly like an instance.
3. **A UI element the plugin built**, still in a visual tree or render tree.

So in a UI host, remove the plugin's view **before** disabling:

```csharp
RemoveTabFor(pluginId);          // and drop your reference to the element
await manager.DisableAsync(pluginId);
```

To verify (development and diagnostics only — it forces GCs):

```csharp
var collected = await manager.WaitForUnloadAsync(pluginId, TimeSpan.FromSeconds(5));
```

Some things are pinned by the frameworks themselves and never collect: a WPF plugin whose view
came from compiled XAML, and a Blazor plugin whose component has been rendered. The plugin is
still stopped and disabled — only the memory is not reclaimed. The full matrix, with the tests
that establish it, is in the [README](../README.md#what-actually-unloads).

## 7. Options reference

```csharp
var options = new PluginManagerOptions { ... };
var manager = new PluginManager(root, services, loggerFactory, options);
```

| Option | Default | Use it when |
| --- | --- | --- |
| `PluginDataRootDirectory` | `<pluginsRoot>/.data` | Plugin state belongs somewhere else — a per-user app-data folder, a writable volume. Each plugin gets `<root>/<id>`. |
| `HostUiCapabilities` | auto-detected | You want deterministic behaviour (tests), or you want to refuse a capability you technically have the assemblies for. `[]` means logic plugins only. |
| `HostCapabilityProbe` | `DefaultHostCapabilityProbe` | You need custom detection. The default checks whether `PresentationFramework` / `Microsoft.AspNetCore.Components` resolve in the default load context. |
| `AdditionalSharedAssemblies` | empty | Plugins resolve *your* service contracts from `IPluginContext.Services`, or implement *your* capability interfaces. Add the assemblies that define them — otherwise the types load twice and every cast fails. |
| `AutoLoadEnabledPlugins` | `false` | You want `ScanAsync` to load enabled plugins itself instead of calling `LoadEnabledAsync`. |
| `InitializeTimeout` | 30s | Your plugins legitimately need longer to start, or you want a tighter leash. The call is made under the manager's lock; the timeout is what stops a hung plugin from deadlocking it. |
| `ShutdownTimeout` | 10s | Same, for shutdown. |

The most important one is `AdditionalSharedAssemblies`. If your plugins are meant to consume
host services:

```csharp
options.AdditionalSharedAssemblies = ["Contoso.PluginApi"];
```

and have plugins reference `Contoso.PluginApi` with `Private="false" ExcludeAssets="runtime"`,
exactly as they reference `Plugman.Contracts`.

## 8. Host recipes

### Console / worker service

```csharp
foreach (var plugin in manager.GetPlugins<ICommandPlugin>())
    Register(plugin.Metadata.Id, plugin.Commands);

var result = await manager.InvokeAsync<ICommandPlugin, string>(id, (p, ct) => p.ExecuteAsync(cmd, args, ct));
```

As a hosted service:

```csharp
public sealed class PluginHostedService(PluginManager manager) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await manager.ScanAsync(ct);
        await manager.LoadEnabledAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => manager.DisposeAsync().AsTask();
}
```

### WPF

```csharp
foreach (var descriptor in manager.DiscoveredPlugins.Where(p => p.IsLoaded && p.UiCapabilities.Contains(PluginUiCapability.Wpf)))
{
    var context = manager.GetPluginContext(descriptor.Id)!;
    var title = manager.TryInvoke<IWpfUiPlugin, string>(descriptor.Id, p => p.ViewTitle);
    var view = manager.TryInvoke<IWpfUiPlugin, FrameworkElement>(descriptor.Id, p => p.CreateView(context));

    var tab = new TabItem { Header = title.Value ?? descriptor.Id, Tag = descriptor.Id, Content = view.Value };
    PluginTabs.Items.Add(tab);

    // Required: a TabControl populated at runtime keeps SelectedIndex at -1, and an unselected
    // tab never realizes its content — the header appears and the panel renders blank.
    if (PluginTabs.SelectedIndex < 0)
        PluginTabs.SelectedItem = tab;
}
```

- Marshal `PluginStateChanged` with `Dispatcher.BeginInvoke`.
- On disable: remove the tab, null its `Content`, then `DisableAsync`.
- Refresh your list *before* awaiting `WaitForUnloadAsync` — that call can take seconds.
- Give lifecycle events and your own status messages separate places in the UI, or they
  overwrite each other.

Full example: [`samples/Plugman.Sample.WpfHost/MainWindow.xaml.cs`](../samples/Plugman.Sample.WpfHost/MainWindow.xaml.cs).

### Blazor Server

```razor
@foreach (var panel in _renderable)
{
    <DynamicComponent Type="panel.ComponentType" Parameters="panel.Parameters" />
}
```

```csharp
_renderable = manager.DiscoveredPlugins
    .Where(p => p.IsLoaded && p.UiCapabilities.Contains(PluginUiCapability.Blazor))
    .Select(p => new
    {
        p.Id,
        Type = manager.TryInvoke<IBlazorUiPlugin, Type>(p.Id, x => x.ComponentType),
        Parameters = manager.TryInvoke<IBlazorUiPlugin, IReadOnlyDictionary<string, object?>?>(p.Id, x => x.DefaultParameters)
    })
    .Where(x => x.Type is { Success: true, Value: not null })
    .ToList();
```

Three things the host must get right:

1. **Put the render-mode boundary on your own component**, e.g. a globally interactive
   `<Routes @rendermode="InteractiveServer" />`. Plugin components render *inside* it. Making a
   plugin component the boundary cannot work — Blazor serializes a boundary component's type by
   assembly name for the circuit to re-resolve.
2. **Serve static assets in every environment.** `WebApplication.CreateBuilder` only wires static
   web assets automatically in Development; without `builder.WebHost.UseStaticWebAssets()` plus
   `app.MapStaticAssets()`, `_framework/blazor.web.js` 404s in Production, the circuit never
   starts, and every plugin panel renders as dead HTML.
3. **Drop cached `ComponentType` values before disabling**, or the load context stays alive.

Full example: [`samples/Plugman.Sample.BlazorServerHost`](../samples/Plugman.Sample.BlazorServerHost).

### ASP.NET Core (non-Blazor)

Nothing special: register the manager as a singleton, scan at startup, and consume logic
capabilities from controllers or minimal-API handlers via `InvokeAsync`. Plugins that need to
serve endpoints should expose a capability of your own design (e.g. `IEndpointPlugin` in your
shared API assembly) rather than trying to register routes themselves.

## 9. Errors and what to show

Nothing about a broken plugin escapes as an exception into your code. Failures land on
`PluginDescriptor.LoadError`:

| Stage | Typical cause | Reasonable host behaviour |
| --- | --- | --- |
| `Manifest` | Malformed/missing `plugin.json`, unknown capability value, failed persist | Show the folder name and the message; the plugin is listed but unusable. |
| `HostCapability` | Plugin needs a UI framework this host does not have | Show it greyed out with the reason. Not an error the operator can fix here. |
| `Dependency` | Missing, disabled, cyclic or failed dependency | Point at the named dependency. |
| `Assembly` / `Activation` | Bad entry assembly or entry type, duplicated contract dll | This is a packaging bug in the plugin; surface `Details` for the author. |
| `Initialize` | The plugin threw or hung on startup | The load was rolled back. Offer a retry (`LoadAsync`). |
| `Capability` | A call threw | The plugin stays loaded and usable; show the message near whatever failed. |
| `Shutdown` | Shutdown threw or hung | Informational — the unload proceeded anyway. |

`LoadError` holds strings only (`Message`, `ExceptionType`, `Details`), never the exception
object — an exception would root the plugin's load context through its stack trace, so the
failure report itself would keep the failing plugin alive. `Message` is safe to show in a UI;
`Details` carries the full chain and stack trace for logs and bug reports.

Only two exceptions ever come out of the manager: `PluginNotFoundException` (you passed an id
that was never discovered — a bug in your code) and `OperationCanceledException` (your token).

## 10. Threading

- The manager is thread-safe; concurrent operations are serialized on an internal gate.
- It `ConfigureAwait(false)`s throughout, so **continuations do not come back on your UI
  thread**. In WPF, `await manager.ScanAsync()` from an event handler does resume on the UI
  thread (your await captures the context), but `PluginStateChanged` handlers do not — marshal
  them yourself.
- Long lifecycle calls are bounded by `InitializeTimeout`/`ShutdownTimeout`, so a hung plugin
  cannot block the gate forever. A plugin that ignores cancellation is abandoned rather than
  waited on: the host keeps running, and that plugin's context is never collected.
- `WaitForUnloadAsync` forces full GCs. Never call it on a hot path or a request thread.

## 11. Trust and security

**Plugman gives you isolation, not a sandbox.** A loaded plugin runs in-process with the host's
full privileges: it can read your files, open sockets, and access anything reachable from the
service provider you handed it. `AssemblyLoadContext` separates *type identity and versions*, not
*permissions* — .NET has no in-process security boundary to offer here.

Consequences for a production host:

- Treat installing a plugin as equivalent to installing an application. Restrict write access to
  the plugins directory to the same people you trust with the host binary.
- Verify what you load if it crosses a trust boundary — Authenticode signatures, hashes, an
  allow-list of ids — before calling `LoadAsync`. Plugman does not do this for you.
- Consider what you pass as `hostServices`. Handing over the application's root container gives
  plugins your database context, your HTTP clients and your configuration, including secrets. A
  small, purpose-built provider containing only the services plugins are meant to use is a
  better default:

  ```csharp
  var pluginServices = new ServiceCollection()
      .AddSingleton(app.Services.GetRequiredService<IClock>())
      .AddSingleton(app.Services.GetRequiredService<ICatalogApi>())
      .BuildServiceProvider();
  ```

- For genuinely untrusted plugins, isolate at the process level (a child process, a container)
  and talk over IPC. That is a different architecture, and Plugman is not it.

## 12. Operating a plugin host

**Layout.** Keep the plugins root outside your application's binary folder if operators are
expected to add plugins — a program-files directory is usually not writable at runtime.

```
/opt/myapp/            app binaries
/var/lib/myapp/plugins app-writable plugins root
/var/lib/myapp/plugins/.data per-plugin state (or set PluginDataRootDirectory)
```

**Deployment.** Adding a plugin is copying a folder plus a rescan; no restart needed. Updating a
loaded plugin needs a disable first, because the dll is locked while loaded on Windows — and if
the plugin has rendered WPF XAML or a Blazor component, plan on a process restart.

**Persistence.** `enabled` lives in each `plugin.json`. That means the plugin folder is the unit
of both code and state, which is simple, but it also means an operator who replaces a folder
wholesale resets the enabled flag to whatever the new manifest ships. If you would rather own
that state centrally, read it from your own store at startup and drive `LoadAsync`/`UnloadAsync`
yourself instead of `LoadEnabledAsync`.

**Logging.** Each plugin logs under `Plugman.Plugin.<id>`; the manager under
`Plugman.Core.PluginManager`. Filter or route per plugin if a noisy plugin is a concern.

**Health.** `DiscoveredPlugins` is a ready-made health surface: report ids where
`HasError` is true, and the count of enabled-but-not-loaded plugins.

## 13. Do and do not

**Do**

- Keep one `PluginManager` per plugins root, as a singleton.
- Drive your UI from `DiscoveredPlugins` descriptors.
- Call plugin code through `TryInvoke` / `InvokeAsync`.
- Remove plugin-built UI from the tree before disabling.
- Dispose the manager on shutdown.
- Put your own capability and service contracts in an assembly listed in
  `AdditionalSharedAssemblies`.

**Do not**

- Cache plugin instances or plugin `Type` objects in fields.
- Reference `Plugman.Contracts.Wpf` or `.Blazor` from shared host code that other hosts use —
  that is exactly what the package split exists to prevent.
- Assume `DisableAsync` frees memory; check `WaitForUnloadAsync` if it matters.
- Call `WaitForUnloadAsync` in production hot paths.
- Hand plugins your root service provider without thinking about [§11](#11-trust-and-security).
- Expect plugin ids to be stable across a plugin rename — they are the key for persisted state.

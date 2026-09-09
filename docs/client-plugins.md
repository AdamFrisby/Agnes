# Client plugins

A client plugin is a DLL dropped into `%APPDATA%/Agnes/client-plugins` (Linux:
`~/.config/Agnes/client-plugins`). The desktop head discovers it at startup, loads it into its own
collectible `AssemblyLoadContext`, and asks it to register what it contributes. Nothing in Agnes
references a plugin, and a plugin that fails to load is skipped rather than taking the app with it.

Only the desktop head loads plugins dynamically. The mobile and web heads compose the same
`IClientPluginModule`s at compile time and report `SupportsDynamicPlugins = false`.

## Writing one

```csharp
public sealed class MyModule : IClientPluginModule
{
    public void Register(ClientPluginCollector collector)
    {
        collector.AddCustomScreen(new MyScreenProvider());              // a tab
        collector.AddViewFactory<MyViewModel>(vm => new MyView { DataContext = vm });  // how to draw it
    }
}
```

`ICustomScreenProvider` says a screen exists; `IViewFactory` says how to render its view-model. They are
separate because `Agnes.Ui.Core` is shared with heads that are not Avalonia, so the contract cannot name a
control type — `IViewFactory` takes and returns `object`, and each head decides what a view is. A plugin
that registers no factory still works: the head falls back to its own templates.

Matching is by **exact** view-model type, so a plugin cannot accidentally claim a base type another plugin
or the head also renders.

Screens registered this way appear in the **New tab** menu. With no plugin screens, New tab stays a plain
button.

## What a plugin must not ship

The loader forces two families of assembly to resolve from the app's own load context, never the plugin's:

- `Agnes.*` — the plugin contract spans `Agnes.Ui.Core`, `Agnes.Abstractions` (`IPluginRegistry`,
  `IEventBus`, `IVoiceProvider`) and `Agnes.Protocol` (`ClientCapabilities`). A plugin carrying its own
  copy would satisfy the compiler and then fail to match at runtime.
- `Avalonia*` — a view the plugin builds must be the *same* `Control` type the head renders, and a second
  copy of Avalonia would duplicate the framework's static state as well.

Reference them with `Private="false"`, and everything else normally — a plugin's own dependencies are
isolated in its load context, so their versions are its own business.

```xml
<ProjectReference Include="..\Agnes.Ui.Core\Agnes.Ui.Core.csproj" Private="false" ExcludeAssets="runtime" />
<PackageReference Include="Avalonia" Version="12.1.0" PrivateAssets="all" ExcludeAssets="runtime" />
<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.10" />  <!-- shipped -->
```

Copies of `Agnes.*` that do end up in the folder are harmless — the loader ignores them — but they are
dead weight.

## Worked example: `Agnes.Plugins.CodeyBox`

Adds a **CodeyBox** tab controlling a [CodeyBox](https://github.com/AdamFrisby/CodeyBox) orchestrator:
the work queue over REST, and the selected item's agent output streamed live from its
`/hubs/agent-stdout` SignalR hub.

It reads its address and key from the same places CodeyBox's own CLI does — `CODEYBOX_CLI_API_URL` /
`CODEYBOX_CLI_API_KEY`, else `~/.config/codeybox/config.json` — so a machine already set up for
`codeybox` needs no second configuration. With no key the tab renders a "configure me" state, because a
machine with no CodeyBox is ordinary rather than broken.

It uses its **own** `HttpClient`, not `AgnesHttp.For(pin)`: that exists for Agnes hosts, which are
typically self-signed and pinned, whereas CodeyBox is a bearer key over plain HTTP.

```bash
dotnet publish src/Agnes.Plugins.CodeyBox -c Release -o ~/.config/Agnes/client-plugins
```

# CAAI_HIVE

## Debugging

CAAI uses the open-source [netcoredbg](https://github.com/Samsung/netcoredbg) Debug Adapter Protocol backend, not VS Code's private debugger installation.

On Windows x64, install the pinned adapter into your user profile:

```powershell
.\Debugging\Install-NetCoreDbg.ps1
```

The installer verifies the official release SHA-256 checksum and requires no administrator privileges. CAAI automatically discovers this installation. Other installations can be selected with `CAAI_DEBUG_ADAPTER` (the full executable path), `AdapterPath` in launch settings, or `netcoredbg` on `PATH`. The adapter must match the runtime/platform architecture.

Select **Run > Select Debug Project** or the folder button in the **Debug** output tab to choose an executable C# project. The sole project in the initial workspace folder is selected automatically. Debug builds use portable PDBs and disable optimization; build and debuggee output appears in **Terminal**. Modified open files require confirmation before they are saved for the build.

| Action | Shortcut |
| --- | --- |
| Start / Continue | F5 |
| Run without debugging | Ctrl+F5 |
| Stop | Shift+F5 |
| Restart | Ctrl+Shift+F5 |
| Pause | F6 |
| Step over / into / out | F10 / F11 / Shift+F11 |
| Toggle breakpoint | F9 or click the editor gutter |
| Build | Ctrl+Shift+B |

Breakpoints are persisted under the user's local application data in `CAAI/debugging/breakpoints.json`. Hollow markers are pending/unverified or disabled; filled red markers are verified by the adapter. Line anchors follow edits, but executing a changed source file requires rebuilding/restarting the session. Use **Debug > Breakpoints** to enable/disable, remove, or set a C# condition. Hover a breakpoint entry for the adapter's diagnostic message.

When paused, **Debug > Call Stack & Variables** lists threads and stack frames. Selecting a frame opens its source, highlights the execution line, and loads that frame's scopes. Expand scopes and variables to fetch children on demand (200 variables per expansion). **Watches** evaluates expressions in the selected frame; expressions and property evaluation may execute debuggee code. Snapshots are invalidated when execution resumes.

**Run > Launch Settings** creates/opens `.caai/launch.json` beside the selected project. Example:

```json
{
	"AdapterPath": null,
	"TargetFramework": "net10.0",
	"WorkingDirectory": ".",
	"Arguments": ["--example"],
	"Environment": { "MY_SETTING": "development" },
	"StopAtEntry": false,
	"JustMyCode": true,
	"ExceptionFilters": []
}
```

Set `TargetFramework` for multi-targeted projects. Paths in launch settings are relative to the project directory. `ExceptionFilters` can include `"all"` to break on thrown managed exceptions; unsupported filter names are rejected with the adapter's supported values. Keep secrets out of checked-in launch settings.

This implementation launches managed projects; attach, native debugging, and interactive debuggee stdin/terminal emulation are not implemented. Unsupported edit-and-continue changes require a restart. The IDE's manual UI-rebuild shortcut is Ctrl+Alt+F5, leaving F5 available for debugging.

## Object Inspector

The notebook inspector and the standalone `ObjectInspectorWidget` share one implementation. `ObjectInspectorWidget1` remains a compatibility subclass for existing notebook callers.

- Expand public fields, explicitly evaluated properties, arrays, lists, and dictionaries.
- Collections load 100 items per page; arbitrary enumerable results require explicit expansion and are limited to 100 items.
- Custom `ToString()` methods are not used for previews. Property getters are evaluated only with **Evaluate getter**; getters and enumerable evaluation may execute user code.
- Filter variable names, types, values, and already-loaded members without evaluating more data.
- Pin root variables, show only pinned variables, and compare values between snapshots. Changed values appear in amber.
- Use the resizable details pane for long values and errors; copy values or expressions through the context menu.
- Cycles and deep object graphs stop expanding rather than recursing indefinitely. Member inspection is limited to 200 public fields and 200 public properties per object; indexers are excluded.

`Inspect(name, value)` adds or replaces a named value. `UpdateVariables(variables)` replaces the notebook snapshot; **Refresh** rereads the current live sources. `Clear()` releases the displayed snapshot and resets pins and filters.

### Debugger Snapshots

Use `SetNodes` with backend-supplied display strings and optional asynchronous child providers. No reflection or debuggee object reference is needed:

```csharp
var order = new ObjectNode("order", "Order", "{Order}",
    cancellationToken => Task.FromResult<IReadOnlyList<ObjectNode>>(
	[new ObjectNode("Id", "int", "42")]));

inspector.SetNodes([order], "Locals");
```

The child provider is called on expansion. Honor its cancellation token and provide bounded or paged child lists. Subscribe to `RefreshRequested` to fetch fresh backend values, and replace the snapshot after a pause or step. `DebugSession` now supplies these snapshots through netcoredbg; the inspector itself remains independent of the debugger transport.
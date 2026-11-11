# CAAI_HIVE

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

The child provider is called on expansion. Honor its cancellation token and provide bounded or paged child lists. Subscribe to `RefreshRequested` to fetch fresh backend values, and replace the snapshot after a pause or step. A debugger adapter must implement the actual debugger connection; the inspector does not start or control a debugging session.
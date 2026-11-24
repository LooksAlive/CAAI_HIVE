# CAAI_HIVE

## Refactoring

Use **F2**, **Rename Symbol...** in the editor context menu, or the rename entry under **Ctrl+.**. Rename follows source symbol references across workspace documents, validates the identifier and rejects new compiler errors/conflicts. It leaves file names, strings, comments and unrelated overloads unchanged. Open tabs synchronize with workspace changes; affected unopened project files are surfaced as unsaved tabs. Review and use **Save All** to persist the changes. Undo is per editor, not a single workspace-wide transaction.

**Ctrl+.** now combines diagnostic fixes with available `CodeRefactoringProvider` actions. The selected text span is passed to providers, enabling selection-based actions such as introducing variables where a bundled Roslyn provider supports them. Discovery is best-effort: providers requiring unavailable IDE dialogs or services may be unavailable. Trusted additional providers can be registered using `EditorRefactoringService.Register(provider)`.

On a supported class, **Extract interface 'IClassName' in this file** generates an interface beside the class and adds it to the class's base list. It copies declared public instance methods and public property accessors, excluding static/private members, constructors and method overrides. Types and default parameter values are semantically expanded so aliases and nested types remain usable. This initial action supports top-level, non-generic, non-partial, non-static classes; records, events, indexers, inherited members and member-selection dialogs are not included. It is not offered if the proposed interface name already exists in the same namespace.

Refactorings and fixes may update text in multiple existing workspace documents. Stale affected-document changes, file creation/removal/moves, and project configuration changes are rejected without partial application. The editor does not automatically write refactored text to disk.

`IIncrementalGenerator` is a different mechanism: it produces additional source during compilation without rewriting the user's source documents. It is appropriate for repeatable generated implementations (serialization, mappings, adapters), not interactive rename or interface extraction. No generator execution pipeline is added here; generator hosting would also need generated-document navigation and generator diagnostic handling.

## Editor Linting And Fixes

Live document and notebook diagnostics now combine compiler diagnostics with available bundled Roslyn IDE analyzers, project analyzer references, and CAAI's in-process rules. Suggestions appear as blue underlines and information entries; warnings remain orange and errors red. Use the existing light bulb or **Ctrl+.** on the diagnostic's line to see available fixes alongside formatting and explicit-type actions. Not every diagnostic has a fix.

| Rule | Default | Action |
| --- | --- | --- |
| `CAAI1001` Redundant boolean comparison | Suggestion | Simplify comparison without changing nullable or user-conversion semantics |
| `CAAI1002` Rethrowing the caught exception | Warning | Replace `throw ex;` with `throw;` when the caught variable has not been reassigned |
| `CAAI1003` Empty catch block | Warning | Review manually; no potentially unsafe automatic rewrite |

Folder loading imports applicable `.editorconfig` files, including ancestor and nested configurations. For example:

```ini
root = true

[*.cs]
dotnet_diagnostic.CAAI1001.severity = suggestion
dotnet_diagnostic.CAAI1002.severity = warning
dotnet_diagnostic.CAAI1003.severity = warning
dotnet_diagnostic.IDE0004.severity = suggestion
```

Reopen the project folder after changing configuration files. Rules left at hidden severity do not appear in the diagnostics list. Notebook scratch projects use default rules unless their workspace is explicitly configured.

`EditorAnalysisService.GetDiagnosticsAsync(document, cancellationToken)` exposes the same document-level analysis programmatically. `EditorAnalysisService.Register(analyzer, codeFixProvider)` adds trusted custom or third-party analyzers and optional fixes. Analyzers attached through `Project.AnalyzerReferences` are also executed; register their code-fix providers separately if needed. Providers and analyzers that fail report details to debug output rather than preventing other providers from running. Bundled provider discovery is best-effort: providers requiring unavailable host services are skipped.

The editor uses an `AdhocWorkspace`, not an MSBuild project loader. Adding `AnalysisLevel` or analyzer NuGet references to a project file alone does **not** load all SDK `CAxxxx`, Roslynator, Sonar or StyleCop rules into this editor. Those require explicitly attaching/registering the corresponding runtime analyzers and fixes. No third-party analyzer packs are installed automatically. Existing-document text fixes can span multiple files; structural project/file changes are rejected visibly rather than partially applied. Fix-all is not exposed.

## Input-To-Code Analysis

`CAAI.CodeAnalysis` connects runtime input objects (including token lists) to potential dependencies in a Roslyn compilation. This is a callable analysis API, not an inspector UI command or execution tracer.

```csharp
var analyzer = new CAAI.CodeAnalysis(compilation);
var result = analyzer.Analyze(tokens, "tokens", parserParameter);

// Alternative entry points for existing local values:
var fromInspector = analyzer.AnalyzeInspector(selectedObjectNode, parserParameter);
var fromEnvironment = analyzer.AnalyzeEnvironment(selectedDataEntity, parserParameter);

foreach (var connection in result.Connections)
{
	var part = result.Parts[connection.InputPartId];
	Console.WriteLine($"{part.Path} -> {connection.Kind}: " +
		$"{connection.FilePath}:{connection.Line}:{connection.Column}");
}

var next = analyzer.Analyze(nextTokens, "tokens", parserParameter);
var counts = result.GetFeatureCounts();
var similarity = result.SimilarityTo(next);
```

`parserParameter` is the `IParameterSymbol` for the parser's input parameter in the supplied compilation. Omit it to infer candidate entry parameters by runtime type, including implemented collection interfaces. Explicit binding avoids treating unrelated methods with the same input type as entry points. Inspector inputs must have an evaluated, non-null `RawValue`; remote debugger display-only snapshots cannot be analyzed by reflection.

Reflection records paths, types, and bounded scalar previews. Public fields and enumerable items are traversed; properties are represented without invoking getters by default. Set `new InputAnalysisOptions { EvaluateProperties = true }` to traverse property values too. Getters and enumeration execute user code, can mutate input, and cannot be forcibly interrupted if they block. Cancellation is checked between operations. Node, depth, collection-item and flow-pass limits bound analysis work; warnings report skipped or truncated data and evaluation failures.

Roslyn flow follows parameter/local aliases, public member reads, collection indexing, `foreach` items, assignments, source call arguments and return values. Connections include member/reference reads, calls, returns and conditions (`if`, conditional expressions, loops and switch inputs), with one-based source lines and columns. Constant integer indices select individual captured items; unknown indices conservatively select all captured items.

Connections are **potential dependencies, not observed execution**. Flow merges assignments and call sites and does not evaluate conditions against token values. External method results conservatively inherit argument dependencies; arbitrary heap mutations, dynamic/reflection calls, delegate dispatch, complete LINQ semantics and runtime branch outcomes are not modeled. `FlowConverged` and `Warnings` must be checked before interpreting results.

Feature counts measure the number of input parts connected to each source location/kind/symbol. `SimilarityTo` computes weighted Jaccard similarity over those counts (0 to 1; no connections scores 0). Compare against the same source revision: offsets are not stable across code edits. This compares dependency structure and multiplicity, not token text equality or measured branch coverage. Result records can be serialized with `System.Text.Json` for later statistics; scalar previews may contain sensitive input.

## Git

The Git icon in the left toolbar opens source control for the current project folder. Git must be installed and available on `PATH`. Use the panel to initialize a repository, inspect staged and working-tree changes and diffs, stage or unstage files, commit, create or switch local branches, delete merged local branches, inspect the latest 100 commits across refs, and fetch, pull, or push. Select a commit and use the document button or double-click to view its details and diff. Pulls are fast-forward only. Remote URLs, upstream branches, credentials, and commit identity must be configured in Git; terminal credential prompts are disabled. The footer shows the branch, change counts, and ahead/behind counts when an upstream is configured.

Git operates on saved files, so save editor changes before staging or committing. Switching branches and pulling require open repository files to be saved; unchanged editor buffers are refreshed afterward. If a file is deleted or edited during the operation, review its open buffer before saving. Use Refresh to pick up changes made outside the IDE.

Select a local branch and use the rebase action to rebase the current branch onto it. Rebase rewrites commits and asks for confirmation. If conflicts occur, resolve and save the files, stage them in the changes list, and use Continue Rebase; Abort Rebase restores the pre-rebase state. Open repository files must be saved before starting, continuing, or aborting a rebase. Git's normal safety checks still apply; branch deletion does not force-delete unmerged branches.

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
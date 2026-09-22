# Semantics, language servers and testing

`src/Stampeded.Core/{Semantics,Roslyn,Lsp,Decompilation,Testing}` and `src/Stampeded.RoslynLsp`.

## The shape of the layer

```
UI (ReviewWorkspace, panes, diff views)
        |
        v
ISemanticProvider  (Stampeded.Core/Semantics/ISemanticProvider.cs)
   |                       |
   |                       +-- LspSemanticProvider -> LspConnection -> child process
   |                                                  (pyright / Stampeded.RoslynLsp)
   +-- RoslynWorkspaceService (in-process MSBuild/Adhoc workspace)
```

Two rules decide everything else:

1. **A symbol is a file plus a position** (`SymbolRef`), never a compiler object. That is the only
   symbol identity a language server can accept back.
2. **One provider instance serves one side (head or base) of one review for one language.** The
   head/base pairing is done above the interface, in `ReviewWorkspace` (`ReviewWorkspace.cs:1939`).

`ReviewWorkspace.SemanticsFor(bool oldSide, string relPath)` (`:1948`) is the dispatch: extension ->
language -> head/base provider; `.cs`/`.csx` fall back to the primary C# pair; everything else gets
`null` and the caller prints "nothing here reads .json files" (`NoProviderMessage`, `:1977`).

## `ISemanticProvider` - the contract

`Semantics/ISemanticProvider.cs:13`, `IDisposable`. No default implementations.

### State

| Member | Semantics |
| --- | --- |
| `SemanticState State` (`:18`) | `NotLoaded / Restoring / Loading / Ready / SyntaxOnly / Failed`. Callers gate compilation-dependent commands on `Ready or SyntaxOnly` (`ReviewWorkspace.IsReady`, `:2152`). |
| `string StateDetail` (`:21`) | One status-bar line: solution name, failure message, or progress. |
| `string LoadLog` (`:24`) | Whole load transcript for the Log pane. `LspSemanticProvider` returns `""` (`:83`) - the server's transcript arrives via stderr into `CliLog` instead. |
| `event Action? StateChanged` (`:26`) | Every state transition. Raised from whatever thread the load runs on; UI subscribers marshal themselves. |

### Path mapping

```csharp
string? ToRelativePath(string absolutePath);   // null when outside the served tree
string ToAbsolutePath(string repoRelativePath);
```

Repo-relative paths are always forward-slashed (git's spelling); absolute paths are the platform's.

### Text overlay

```csharp
void SetTextOverlay(IReadOnlyDictionary<string, string> textByRelativePath);
void ClearTextOverlay();
Task<string?> GetDocumentTextAsync(string relPath, CancellationToken ct);
```

The overlay is how "review one commit of a file that later commits change" works: positions are
offsets into a *specific* text, so the whole stack must agree which one. `GetDocumentTextAsync`
exists so a view can check the provider's text is the text on screen before applying positions.

### Positions and tokens

```csharp
Task<int?> GetPositionAsync(string relPath, int line, int column, CancellationToken ct);
Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(string relPath, CancellationToken ct);
Task<IReadOnlyList<SemanticToken>> GetSemanticTokensForTextAsync(string relPath, string text, CancellationToken ct);
```

`GetSemanticTokensForTextAsync` classifies text the provider does not hold (a historical revision).
Roslyn forks the document (`:443`); LSP *declines* unless the text equals what the server holds (`:369`).

### Queries at a position

```csharp
Task<string?>    GetQuickInfoAsync(string relPath, int position, CancellationToken ct);  // one line
Task<string?>    GetHoverTextAsync(string relPath, int position, CancellationToken ct);  // full, with docs
Task<SymbolRef?> GetSymbolAtAsync(string relPath, int position, CancellationToken ct);
Task<SymbolRef?> GetSymbolOnLineAsync(string relPath, int line, int preferredColumn, CancellationToken ct);
Task<SymbolRef?> GetEnclosingMemberAsync(string relPath, int line, CancellationToken ct);
```

`GetSymbolOnLineAsync` falls back to any identifier on the line - a caret usually sits in the
indentation. `GetEnclosingMemberAsync` answers "which member is this line in", which on a body line
is *not* the token under the caret.

### Queries about a symbol

```csharp
Task<SymbolLocation?>             GetDefinitionAsync(SymbolRef, CancellationToken);
Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(SymbolRef, CancellationToken);
Task<IReadOnlyList<SemanticToken>> FindOccurrencesInFileAsync(SymbolRef, string relPath, CancellationToken);
Task<IReadOnlyList<CallNode>>     GetCallsAsync(SymbolRef, CallDirection, CancellationToken);
Task<IReadOnlyList<DeclarationHit>> FindDeclarationsAsync(string pattern, int max, CancellationToken);
```

### Queries about a file

```csharp
Task<IReadOnlyList<ChangedMember>>     MapLinesToMembersAsync(string relPath, IReadOnlyCollection<int> lines, CancellationToken);
Task<IReadOnlySet<string>>             ListMemberDisplaysAsync(string relPath, CancellationToken);
Task<IReadOnlyList<OutlineNode>>       GetOutlineAsync(string relPath, string sideText, CancellationToken);
Task<IReadOnlyList<MemberFoldRegion>>  GetFoldRegionsAsync(string relPath, string sideText, CancellationToken);
```

The `sideText` parameter on the last two is load-bearing (`:99`): one side of a diff is another
revision, and an outline drawn at the wrong lines is worse than none. A provider that only knows the
revision it holds must return `[]` when `sideText` differs. Roslyn sidesteps the problem by parsing
`sideText` itself - those two are pure functions (`RoslynWorkspaceService.cs:1112`), and the views
exploit it: `Task.IsCompletedSuccessfully` lets them keep a synchronous path.

### Threading and async expectations

- Every method is cancellable; **no method has an internal timeout**. `LspConnection.RequestAsync`
  waits forever unless the caller's token fires (`LspConnection.cs:207`). Most UI call sites pass
  `CancellationToken.None` (`ReviewWorkspace.cs:2176, 2183, 2200, 2302, 2336, 2354, 2400`). A wedged
  server therefore wedges those awaits permanently. **This is the layer's biggest hazard.**
- Implementations are not documented as thread-safe, and `LspSemanticProvider` is not.
- `LspSemanticProvider.Dispose` disposes the underlying connection (`:726`), so the head and base
  providers sharing one connection (the Roslyn-LSP case) must not both dispose it - in practice
  `LspConnection.disposed` guards the second call (`:376`).

### `IDecompileTargets` (`:115`)

```csharp
Task<DecompileTarget?> GetDecompileTargetAsync(SymbolRef symbol, CancellationToken ct);
```

Deliberately *not* on `ISemanticProvider`: only a provider with real metadata behind it can answer.
Callers test `sem is IDecompileTargets` (`ReviewWorkspace.cs:2263`).

## `SemanticTypes.cs` - the vocabulary

All records, all 1-based lines and columns.

| Type | Line | Carries |
| --- | --- | --- |
| `SemanticState` | 4 | the enum above |
| `SymbolLocation(FilePath, Line, Column, Length)` | 16 | absolute path as the provider knows it |
| `ReferenceHit(FilePath, Line, Column, Length, LineText)` | 19 | `LineText`, so the references pane needs no second read |
| `SemanticToken(Line, Column, Length, Classification)` | 23 | `Classification` is a **Roslyn classification name** (`"class name"`, `"method name"`), whoever produced the token - that is what the editor's colour table keys on |
| `ChangedMember(Display, Kind, FirstLine)` | 26 | symbol-level change map |
| `DeclarationHit(Name, Container, Kind, RelPath, Line)` | 29 | go-to-symbol-by-name |
| `CallDirection` | 31 | `Callers` / `Callees` |
| `CallSite(FilePath, Line, Preview)` | 41 | one actual call, not a signature |
| `CallNode(Display, ContainingType, FilePath?, Line, Column, Sites)` | 48 | `CanExpand => FilePath is {Length:>0}`; a metadata-only member is a leaf |
| `SymbolRef(RelPath, Line, Column, Display, Name, IsType, ContainingType?)` | 70 | see below |
| `OutlineNode(Kind, Title, StartLine, EndLine, Children)` | 80 | structure tree |
| `MemberFoldRegion(StartLine, EndLine, HeaderEndLine)` | 87 | `HeaderEndLine` = last line of the declaration itself (the one carrying `{` or `=>`) |
| `DecompileTarget(AssemblyPath, ReflectionName, MetadataToken, TypeName)` | 91 | |

### Why `SymbolRef` is a position

Documented at `:59`. Nothing else survives leaving a compiler's memory. A language server names a
symbol by "the position that resolves to it" and nothing more, so that is what every provider can
accept back.

The consequence to preserve when touching `RoslynWorkspaceService`: **the position stored in a
`SymbolRef` has to re-resolve to the same symbol.** `MakeRef` stores the query position (`:995`);
`DeclarationRef` stores the declaration's own name-token position (`:1013`); `GetSymbolOnLineAsync`
prefers `DeclarationRef` precisely because the position that found the symbol is not reported back
(`:1057`).

`Display` uses `SymbolDisplayFormat.CSharpShortErrorMessageFormat` (e.g. `Foo.Bar(int)`) and is
compared against the change map (`ReviewWorkspace.IsChangedMember`, `:2373`) - **it must stay
stable**. `ContainingType` is null for a type itself and for members whose type is metadata-only.

## RoslynWorkspaceService - in-process C# semantics

`Roslyn/RoslynWorkspaceService.cs`, 1131 lines, implements `ISemanticProvider` and `IDecompileTargets`.

### Fields and lifecycle

```csharp
Workspace? workspace;        // MSBuildWorkspace or AdhocWorkspace
bool ownsWorkspace;          // false for a derived (base-side) view
Solution? solution;          // current, possibly overlaid
Solution? loadedSolution;    // what was loaded, overlay-free
Dictionary<string, DocumentId>? documentsByPath;   // absolute path -> id, OrdinalIgnoreCase
string worktreePath = "";
```

One instance per review session per side; dispose and reload on PR switch, never patch
incrementally (`:13`). `Dispose` (`:1121`) disposes the workspace **only if `ownsWorkspace`** - a
derived base view shares the head's workspace, and disposing it would take the head down.

### Head load - `LoadAsync(worktree, chosenSolution, ct)` (`:118`)

1. `SolutionTarget.ForSemantics(worktree, chosenSolution)` picks the solution (`.sln`, `.slnx`, and
   for a `.slnf` filter the solution the filter names - Roslyn opens a solution, not a filter).
2. `Restoring` -> `RestoreAsync(sln, cleanRetry: false)`.
3. On `ToolFailedException`: log, `Restoring "clean retry"`, `RestoreAsync(sln, cleanRetry: true)`
   (`:134`). Causes named in the comment: a stale `packages.lock.json` on the PR branch, a broken
   `obj/` from an interrupted restore in the cached worktree.
4. `Loading` -> `MSBuildWorkspace.Create()` + `OpenSolutionAsync`; every `msbuild.Diagnostics` entry
   goes into `LoadLog`.
5. `DropUnresolvedAnalyzers(loaded)`.
6. If any project has any document: adopt, `IndexDocuments()`, `Ready`. Otherwise dispose the
   MSBuild workspace and fall through.
7. `LoadSyntaxOnly(worktree, ct)`.
8. `OperationCanceledException` rethrows; **any other exception degrades to syntax-only** (`:168`),
   and if even that throws, `Failed`.

**`RestoreAsync`** (`:210`):

```
normal:      dotnet restore <sln> -p:RestoreEnablePackagePruning=false
clean retry: DeleteBuildArtifacts(worktree) first, then
             dotnet restore <sln> -p:RestoreEnablePackagePruning=false \
                                  --force --force-evaluate -p:RestoreLockedMode=false
```

Environment: `OPENSSL_ENABLE_SHA1_SIGNATURES=1` plus `ExternalTool.StripMsBuildLocatorVariables`, so
the child does not inherit this process's pinned MSBuild. Pruning is disabled because it would
rewrite a committed `packages.lock.json` (ILSpy carries full lock files). NuGet reports errors on
**stdout**, so both streams are captured and tail-truncated to 4000 chars; when both are empty,
`LogHostDiagnosticsAsync` (`:251`) dumps `PATH`, `DOTNET_ROOT`, `DOTNET_HOST_PATH`,
`MSBUILD_EXE_PATH`, `MSBuildSDKsPath`, `MSBuildExtensionsPath` and `dotnet --version`.

**`DropUnresolvedAnalyzers`** (`:192`) - subtle and important. An analyzer whose file is missing
stays in the project as an `UnresolvedAnalyzerReference`, and *checksumming* one throws. Every
`FindReferences` call checksums the solution, so one missing analyzer path kills Shift+F12 for the
whole session. The fix drops any reference whose `FullPath` does not exist.

**`DeleteBuildArtifacts(root)`** (public static, `:278`) - deletes every `obj`/`bin` below `root`,
deepest first, `IOException` swallowed. `AttributesToSkip = FileAttributes.ReparsePoint` is
mandatory: review worktrees link their submodules back to the real clone, and a walk descending
through such a link once deleted committed fixture binaries in the user's own checkout. Covered by
`BuildArtifactCleanupTests.cs`.

**`LoadSyntaxOnly`** (`:300`) - `AdhocWorkspace`, one project `"Worktree"`, every `*.cs` below the
worktree except paths containing `/obj/` or `/bin/`, metadata references from
`AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` (`:321`). Ends in `SyntaxOnly`.

**`IndexDocuments`** (`:330`) - `documentsByPath` keyed `OrdinalIgnoreCase`; `TryAdd`, so the first
project wins for linked or multi-targeted files.

### Base load - `LoadFrom(head, replaced, removed, added)` (`:62`)

The base side is **not** a second checkout, restore and design-time build. It is the head's own
immutable `Solution` with documents swapped:

- shares `head.workspace` and does **not** own it;
- copies `head.documentsByPath`, so its own mutations do not leak back;
- `replaced`: `derived.WithDocumentText(id, SourceText.From(text))`;
- `removed`: `documentsByPath.Remove` + `derived.RemoveDocument(id)`;
- `added` (files only the base revision has - deletions and rename sources): a new `DocumentId` in
  **the project whose directory reaches furthest down towards the file** (`:97`), because a file no
  longer in the tree has no project of its own;
- sets both `solution` and `loadedSolution` to the derived one, so a later overlay starts from the
  *base* revision rather than the head's;
- copies the head's `State`/`StateDetail`.

The caller assembles the three maps in `ReviewWorkspace.BaseSideTextsAsync` (`:625`) from the object
database via `Blobs.ReadAsync(baseSha, ...)`.

### Paths

- `ToAbsolutePath` (`:382`) = `Path.GetFullPath(Path.Combine(...))`. The `GetFullPath` is required:
  git speaks forward slashes everywhere, `Path.Combine` does not normalise the ones already there,
  and on Windows the index is keyed on Roslyn's `src\Foo.cs`.
- `ToRelativePath` (`:391`) - `OrdinalIgnoreCase` on Windows only, and it explicitly checks that the
  character after the root is a separator, so `/repo-other` is not "inside" `/repo`. Returns
  forward-slashed.

### Operation by operation

| Operation | Implementation |
| --- | --- |
| semantic tokens | `ClassifyAsync` (`:450`): `Classifier.GetClassifiedSpansAsync` over the whole text, keep only `IsIdentifierClassification` (`:547` - 18 `ClassificationTypeNames` values), drop multi-line spans, emit 1-based tokens, `DistinctBy((Line,Column,Length))`, ordered. The for-text variant forks the document with `document.WithText(...)` so the project's references and other files still resolve (`:443`). |
| quick info | `QuickInfoService.GetQuickInfoAsync`, sections joined with `\n\n` (`:488`). |
| hover | A *different* path: resolve the symbol, `ToDisplayString(CSharpErrorMessageFormat)` plus the `<summary>` extracted from `GetDocumentationCommentXml` by two regexes (`ExtractSummary`, `:975`). |
| go to definition | `ResolveAsync` (re-resolve the `SymbolRef` at its position) -> `DefinitionLocationOf` (`:814`): the first in-source location of `symbol.OriginalDefinition`. Null when metadata-only, and the caller then tries decompilation. |
| find references | `SymbolFinder.FindReferencesAsync(symbol, solution, ct)` -> `ReferencesOfAsync` (`:841`). Each hit reads its tree's text for the line preview; dedup on `(FilePath, Line, Column)`, ordered by path then line. |
| occurrences in file | `OccurrencesOfAsync` (`:508`): `FindReferencesAsync` scoped to one document; classification is `"reference"` or `"definition"` (definitions matched by comparing `location.SourceTree` with the document's syntax tree). |
| call hierarchy | `GetCallersAsync` (`:880`) = `SymbolFinder.FindCallersAsync` over the solution, indirect callers kept and *not* marked apart. `GetCalleesAsync` (`:898`) walks the member's `DeclaringSyntaxReferences`, keeps `InvocationExpressionSyntax` and `ObjectCreationExpressionSyntax`, groups sites by `target.OriginalDefinition` with `SymbolEqualityComparer.Default`. Both end in `Order` (dedup, sort by containing type then display). |
| go to symbol by name | `FindDeclarationsAsync` (`:708`): `SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, pattern, SymbolFilter.TypeAndMember, ct)` - Roslyn's own matcher, so prefix, substring and camel-hump all work (`"RWS"` finds `RoslynWorkspaceService`). Source declarations only; `IsImplicitlyDeclared` skipped; capped at `max`. |
| outline / folds | Delegated to `DocumentOutline.Compute` / `MemberFolding.Compute` on `sideText` - pure, synchronous, `Task.FromResult` (`:1112`). |
| enclosing member | `FindEnclosingMemberAsync` (`:682`) -> `MemberAtPosition` -> `DeclarationRef`. |
| change map | `MapLinesToMembersAsync` (`:590`) per line: skip blank lines, take the position at the first non-whitespace character, `MemberAtPosition`, key by display, keep the smallest line. `ListMemberDisplaysAsync` (`:560`) walks `MemberDeclarationSyntax` nodes; a field declaration declares nothing itself, so each `Variable` is asked separately. |
| decompile target | `GetDecompileTargetAsync` (`:1091`): resolve -> `OriginalDefinition` -> climb to the **top-level** containing type -> `TryGetMetadataAssemblyPath` (`:826`, scanning *already realized* compilations only, via `project.TryGetCompilation` + `compilation.GetMetadataReference(assembly)`) -> `DecompileTarget(assemblyPath, ns+MetadataName, original.MetadataToken, topType.Name)`. |

**`MemberAtPosition`** (`:641`) is the trickiest piece. Walking outward from
`root.FindToken(position).Parent`:

- hitting a `BlockSyntax` or `ArrowExpressionClauseSyntax` first -> break out and use
  `model.GetEnclosingSymbol(position)`, which correctly reports a local function or lambda;
- hitting a `MemberDeclarationSyntax`/`LocalFunctionStatementSyntax` first (the position is in a
  *header* - a signature, a `class C`, an attribute) -> `GetDeclaredSymbol(node)`, because a
  member's own header is not inside the scope it opens, and a signature is exactly what a diff of a
  changed member touches.

`WalkToMember` (`:665`) then climbs `ContainingSymbol` until it reaches a method, property, field,
event or named type, maps accessors to their `AssociatedSymbol`, and returns null for a namespace.
Covered by `EnclosingMemberTests.cs`.

### DocumentOutline

`CSharpSyntaxTree.ParseText` - syntax only, resilient to broken code, no workspace needed.
Namespaces are **flattened** (`:31`). Titles: `class Foo<T>` for types, `Bar(int, string)` for
methods (parameters rendered as *types*, falling back to the identifier), `~Foo()`,
`operator +(...)`, `implicit operator Foo`, `this[int]`, plain identifiers for properties, events
and fields; field and event-field declarations emit one node per variable. Lines are 1-based
inclusive from `syntax.Span`.

### MemberFolding

Foldable node kinds (`:21`): `BaseTypeDeclarationSyntax`, `BaseMethodDeclarationSyntax`,
`BasePropertyDeclarationSyntax`, `EventFieldDeclarationSyntax`, `LocalFunctionStatementSyntax`.

- `DeclarationStart` (`:101`) skips attribute lists - folding from the attribute would hide the one
  thing a collapsed member cannot say for itself. A member that is one line once attributes are
  excluded stops folding.
- `HeaderEnd` (`:79`) finds the token that opens the body: for a type the `OpenBraceToken`; for
  anything else the first token of the first child that is a `BlockSyntax`,
  `ArrowExpressionClauseSyntax` or `AccessorListSyntax`. Asking child *nodes* is what keeps an `=>`
  in a parameter default or a base list from being mistaken for the body opener. No body means the
  header ends where it starts.
- `AddRegionDirectives` (`:47`) folds `#region` to `#endregion`, starting the fold at the *end* of
  the `#region` line so the label stays visible. Regions are not bound by syntax nesting, so a
  region crossing a member fold is **dropped** (`Crosses`, `:66`) rather than allowed to corrupt the
  set - no folding manager can represent crossing folds.

### MemberRelocation

Not part of `ISemanticProvider`. It backs comment anchoring (`src/Stampeded/ReviewComments.cs:294`):
when a comment's line no longer exists, text-and-context matching finds nothing and the remark is
falsely reported outdated although the member it is about is right there.

`Locate(oldText, oldLine, newText, lineText)` -> `MemberMove(Line, Member, FoundTheLine)`:

1. `PathTo(DocumentOutline.Compute(oldText), oldLine)` - the chain of outline nodes containing the
   line, outermost first. **Empty means null** (a using directive or file-level comment has no
   member to follow).
2. `Find` (`:88`) walks the same chain in the new outline via `Best` at each level; failing that,
   `Anywhere` searches the whole new tree for the innermost member - a type rename, or a member
   moving between types of one file, leaves the member intact.
3. `Best` (`:112`): an exact `(Kind, Title)` match wins; otherwise same kind and same `Name(title)`
   (the name without its parameter list), tie-broken by `Shared` - the length of the common prefix
   of the two signatures, which separates overloads without pretending to compare types.
4. Inside the found member, look for a line whose trimmed text equals `lineText`, preferring the
   candidate closest to `oldLine` (which is what tells two identical lines of one member apart)
   -> `FoundTheLine: true`.
5. Otherwise place it at the same *offset* into the member (`oldLine - oldPath[^1].StartLine`); if
   that runs past the member's end, use its first line - the declaration is what the comment is
   about once the statement it named is gone, and a closing brace says nothing.

Nothing requires the two texts to be related: it works across a rebase or force-push as long as the
old blob is still readable. Tests: `MemberRelocationTests.cs`.

## The LSP client

### Framing - `LspStream`

Shared by both ends (client and our own server), which is why it lives apart from either.

- `WriteMessageAsync`: `Content-Length: N\r\n\r\n` plus N UTF-8 bytes, then `FlushAsync`.
- `ReadMessageAsync`: header, then a `while (read < length)` loop; `null` at end of stream.
- `ReadHeaderAsync` (`:40`) reads **one byte at a time** - deliberate: any buffering would eat into
  the body. `\r` dropped, `\n` ends a line, an empty line ends the header block and returns the last
  `Content-Length` seen (`-1` at EOF).

### `LspConnection`

`LspServerSpec(Name, Executable, IReadOnlyList<string> Arguments)` (`:12`).

**Start-up - `StartAsync(spec, rootPath, ct, initializationOptions, settings)`** (`:73`):

- `ProcessStartInfo` with all three streams redirected, `UseShellExecute = false`, and
  **`CreateNoWindow = true`** - on Windows a console child of a GUI process gets its own window, and
  npx reaches the server through cmd, so without this the reader gets black windows in their face.
- Removes `MSBUILD_EXE_PATH`, `MSBuildSDKsPath` and `MSBuildExtensionsPath` from the child
  environment: this process pins them, and a server that runs `dotnet` would get the wrong SDK.
- A start failure becomes `CliLog` plus `ToolFailedException(executable, -1, message)`. This is the
  common case (a server nobody installed) and callers catch exactly this
  (`ReviewWorkspace.cs:2059, 2135`).
- Starts `PumpStdErrAsync` and `ReadLoopAsync`, each wrapped in `HandleFailure` (`:439`), which logs
  `connection FAILED: ...` on fault.
- Sends `initialize` with `processId`, `rootUri`, `capabilities`, `trace` (`"verbose"` when tracing,
  else `"off"`), `initializationOptions` and `workspaceFolders`; stores `capabilities.Clone()`; logs
  `ReportWhatItCanDo`; sends `initialized`.

**Client capabilities** (`:185`) are deliberately small, with **no dynamic registration**:
`textDocument.{synchronization(didSave:false, willSave:false), definition(linkSupport:false),
references, hover(plaintext+markdown), documentHighlight, documentSymbol(hierarchical),
semanticTokens(full, relative), callHierarchy}`, `workspace.{symbol, workspaceFolders,
configuration}`, `window.workDoneProgress`.

**`ReportWhatItCanDo`** (`:145`) logs the server's `serverInfo` (falling back to `spec.Name`,
because pyright does not name itself) and, crucially, the list of the eight capabilities the review
needs that the server does *not* advertise - a missing capability answers nothing forever, which is
otherwise indistinguishable from a broken setup.

**Request/response correlation** (`:207`):

```csharp
public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
```

- `id = Interlocked.Increment(ref nextId)`; the first id is 1 - note that `shutdown` in `Dispose`
  uses id `0`, so there is no collision.
- `pending[id] = (TaskCompletionSource<JsonElement>(RunContinuationsAsynchronously), method)`; the
  method name is stored so failures log as `textDocument/definition FAILED: ...` rather than
  `request 7 failed`.
- `ct.Register(() => completion.TrySetCanceled(ct))`; on `OperationCanceledException` it sends
  `$/cancelRequest` and rethrows.
- `finally { pending.TryRemove(id, out _); }`
- Logging: while tracing, every request with elapsed ms and `Summarize(result)` (kind plus array
  length, or a 200-char truncation); otherwise only requests over **500 ms**.
- **If `disposed`, returns `default(JsonElement)` silently.**

**Dispatch** (`:272`):

- `id` and no `method` is a response. An unknown or removed id is dropped. An `error` member is
  logged and the waiter completed with `default` - so **a server error is indistinguishable from
  "found nothing" at the call site**, and the log is the only record.
- `method` with `id` is a server-to-client request, handled by `AnswerServerRequest`.
- `method` without `id` is a notification: `window/logMessage` is copied into `CliLog`, and every
  notification is re-raised on the `Notification` event.

**`workspace/configuration`** (`:327`) answers one entry per requested item, **in the requested
order**, using `settings(section)` or `new object()` for sections we know nothing about, and logs
the whole answer. The comment at `:308` explains why nothing is declined by silence: a server that
asked and heard nothing waits, and everything behind it waits too. Responses go through
`ResponseJson` (`:36`), which does **not** drop nulls - a JSON-RPC response with neither result nor
error is malformed.

**Shutdown** (`:374`): `disposed = true`, cancel `stopping`, `shutdown` (wait 500 ms), `exit`
(200 ms), `WaitForExit(1000)`, then `Kill(entireProcessTree: true)`. The polite shutdown exists so a
server mid-write to its cache does not leave it broken.

**Tracing** (`:136`): `STAMPEDED_LSP_TRACE` set and not `"0"`. Turns on per-request logging, asks the
server for verbose tracing at initialize, and raises pyright's `logLevel` to `Trace`.

**`LspUri`** (`:407`):

- `FromPath` = `new Uri(path).AbsoluteUri`.
- `ToPath` (`:421`) un-escapes `%3A`/`%3a` **before** parsing, because pyright and everything else
  built on vscode-uri writes `file:///d%3A/src/app.py`, which `Uri` does not recognise as naming a
  drive; then strips a leading separator in front of `X:`. Returns `null` for non-file URIs (a
  decompiled or generated document the server invented).

### Document URIs and `?side=base`

`LspSemanticProvider.Uri(relPath)` (`:76`):

```csharp
LspUri.FromPath(ToAbsolutePath(relPath)) + (UriSide.Length > 0 ? "?side=" + UriSide : "")
```

`UriSide` is `""` for every server we did not write - those are rooted at one revision - and
`"base"` for the second `LspSemanticProvider` built on the **same** Roslyn-LSP connection
(`ReviewWorkspace.cs:2055`). It is a query on a file URI rather than a scheme of its own, so
everything that merely wants the path still reads one.

### `LspSemanticProvider`

State: `openDocuments` (relPath -> `TextIndex`), `symbolsByPath` (relPath -> flattened document
symbols), `overlay` (relPath -> text), `tokenTypes` (the server's semantic-token legend).

- **Initial state** (`:37`): `Loading` if the server advertises
  `experimental.loadsAsynchronously`, else `Ready`. Our Roslyn server sets that flag and then pushes
  `stampeded/state` notifications, handled in `OnNotification` (`:53`).
- **`Open(relPath)`** (`:106`) is the gate on everything: servers answer about what they were told,
  not about what is on disk. First use reads the overlay or the file, builds a `TextIndex`, logs
  `opened <path> (<languageId>, <n> line(s))`, and sends `textDocument/didOpen`. `LanguageIdOf`
  (`:143`) maps `.py`/`.pyi` to `python`, `.cs` to `csharp`, plus `.ts`, `.js`, `.go`, `.rs`, else
  `plaintext`.
- **Overlay** (`:153`): `SetTextOverlay` replaces the map and re-sends every already-open document;
  `Resend` swaps the whole text (`didChange` with a single full `contentChanges` entry, version
  hard-coded to 2), rebuilds the `TextIndex` and **invalidates `symbolsByPath`**. Incremental sync
  would save bytes on a keystroke; nothing here types.
- **Symbols are words** (`:194`): there is no request that answers "what is the token here called",
  so `GetSymbolAtAsync`/`GetSymbolOnLineAsync` take the identifier under the position from
  `TextIndex.WordAt`, falling back to `FirstWordOn` for a caret in the indentation. `Make` builds a
  `SymbolRef` with `Display == Name == word`, `IsType: false`, no containing type.
- **Enclosing member** (`:214`) comes from document symbols instead: `Innermost` plus the nearest
  containing type-kind symbol.
- **Positional requests** map 1:1 to `textDocument/definition`, `references` (with
  `includeDeclaration: true`), `documentHighlight` (kind `3` = Write is `"definition"`, else
  `"reference"`) and `hover`. `GetQuickInfoAsync` simply calls `GetHoverTextAsync` (`:303`) - the
  full documentation is what a reader opened the tooltip for.
- **Semantic tokens** (`:333`) decode the protocol's relative 5-tuple encoding (`deltaLine,
  deltaStartChar, length, tokenType, tokenModifiers`), where `deltaStartChar` is relative only when
  `deltaLine == 0`. Types outside the legend are skipped; `LspSymbolKinds.ClassificationOf` returns
  `null` for keywords, strings and comments, which the grammar already colours and which a second
  opinion would only fight with.
- **`Holds(relPath, sideText)`** (`:528`) is the revision guard for outline, folds and for-text
  tokens: it compares the server's text with the text on screen after
  `ReplaceLineEndings("\n").TrimEnd('\n')`, because no server preserves line endings or a trailing
  newline faithfully.
- **Folds from document symbols** (`:515`): `MemberFoldRegion(StartLine, EndLine, max(StartLine,
  SelectionLine))` - a document symbol says nothing about where the brace or colon is, only where
  the name is.
- **Outline** (`:482`, `ToOutline` `:495`) drops variable-kind children of callable parents: a
  server reports every local in a function body, and an outline is for finding a place to jump to.
- **Call hierarchy** (`:427`): `prepareCallHierarchy` -> item `[0]` ->
  `callHierarchy/incomingCalls` or `outgoingCalls`; the other end is `from`/`to` respectively.
  `fromRanges` are sites in the *caller's* file for incoming calls and in the *asked-about* file for
  outgoing (`:458`). The node display is re-derived from document symbols via `DisplayOfAsync`
  (`:567`) so it matches how the change map names the same member (`Greeter.greet`, not `greet`) -
  the two are compared to tint calls the review touches.
- **`FindDeclarationsAsync`** is `workspace/symbol`, capped at `max`.
- **`GetDecompileTargetAsync`** (`:618`) sends `stampeded/decompileTarget`, but only when the server
  advertises `experimental.decompileTarget`.
- **`Flatten`** (`:575`) handles both reply shapes: hierarchical `DocumentSymbol` (has `range`,
  `selectionRange`, `children`) and flat `SymbolInformation` (has `location.range`, no children).
- **`LineTextOf`** (`:695`) reads a file for its text only and caches the `TextIndex` **without**
  telling the server about it - a references search would otherwise open half the repository.

**Thread-safety caveat.** `openDocuments`, `symbolsByPath` and `overlay` are plain `Dictionary`s
mutated from `async` methods (`DocumentSymbolsAsync` writes at `:557` *after* an await). This is only
safe as long as every call originates on one thread. There is no lock and no `ConcurrentDictionary`;
treat it as a UI-thread-affine object.

### TextIndex and LspSymbolKinds

`TextIndex`: line starts computed once by scanning for `'\n'`; `OffsetOf` (clamped to line length),
`LineColumnOf` (binary search), `LineText` (drops a trailing `\r`), `WordAt`
(`char.IsLetterOrDigit || '_'`), `FirstWordOn` (first word character that is not a digit). All
1-based; LSP's 0-based pairs are converted at the request boundary.

`LspSymbolKinds` translates the two protocol vocabularies into the ones the review already uses:
`Names[]` for `SymbolKind` (a number in the protocol and nothing else), the predicates `IsType`,
`IsCallable` and `IsVariable`, `OutlineKindOf` (icon keys - a Python `def` reads as a `method`;
`Module`, `Namespace` and `Package` read as `class`), and `ClassificationOf` (LSP token type to
Roslyn classification name, where `null` means "no colour of ours").

## LanguageServers

`ExtensionsByLanguage` (`:17`) currently holds only `python: {.py, .pyi}`. It decides whether a
review pays for a server at all (`ReviewWorkspace.LoadOtherLanguagesAsync`, `:2072`).

### Python lookup order - `Python()` (`:27`)

1. **`STAMPEDED_PYTHON_LSP`** - a whole command line (`"pylsp"`, `"npx basedpyright-langserver
   --stdio"`), split on spaces, the first token resolved through `OnPath` where possible. Logged,
   with `" - WHICH DOES NOT EXIST"` appended when the resolved executable is not there
   (`FromEnvironment`, `:148`).
2. **On PATH**, in order: `pyright-langserver --stdio`, `basedpyright-langserver --stdio`,
   `jedi-language-server`, `pylsp`.
3. **This tool's own install**: `Installed()` (`:72`) -
   `<cache>/python-lsp/{bin|Scripts}/basedpyright-langserver[.exe]`.
4. **npx**: `npx --yes --package pyright -- pyright-langserver --stdio`. The explicit `--package` is
   required - npx cannot infer the package `pyright` from the binary `pyright-langserver` and fails
   with a 404 otherwise.
5. `null`, with a log line saying one can be installed.

`ReviewWorkspace` then falls through to installing one when the spec is null *or* fails to start
(`ReviewWorkspace.cs:2084`); if that also fails it posts a status message and opens the Log pane.

### Bootstrap install - `InstallPythonAsync(repoPath, ct)` (`:92`)

Cache root is `CachePath.For("python-lsp")` = `$XDG_CACHE_HOME/stampeded/python-lsp`, falling back
to `~/.cache` (non-Windows) or `LocalApplicationData` (Windows).

```
python -m venv <cache>/python-lsp
<cache>/python-lsp/bin/python -m pip install --disable-pip-version-check basedpyright
```

Both go through `ExternalTool.RunAsync`, so both appear in the log; if `Installed()` already exists
it returns immediately; a `ToolFailedException` is logged and returns null. The interpreter comes
from `PythonEnvironment.InterpreterFor(repoPath)`, and when there is none, nothing is installed.

**basedpyright rather than pyright** because it ships as a wheel carrying its own node, so a machine
with Python and no node still gets a working server - the case npx cannot serve. It is pyright
underneath. Nothing is added to the reader's Python or PATH; deleting the directory undoes all of
it. Test: `PythonServerInstallTests.cs`.

### `Roslyn()` (`:126`)

`STAMPEDED_CSHARP_LSP` first; then `Stampeded.RoslynLsp[.exe]` beside `AppContext.BaseDirectory`;
then the source-build sibling
`../../../../Stampeded.RoslynLsp/bin/{Debug|Release}/net10.0/Stampeded.RoslynLsp[.exe]`, with the
configuration guessed from whether the current output path contains `/Release/`.

### The Windows PATHEXT problem - `OnPath` / `ExecutableNames` (`:163`, `:190`)

PATH is walked by hand, rather than left to `Process.Start`, so the log can say which executable was
picked. For each directory, `ExecutableNames` yields the candidates:

- **not Windows, or the path already has an extension** - the path itself, and nothing else;
- **Windows, no extension** - `path + ext` for every entry of `PATHEXT` (default
  `.COM;.EXE;.BAT;.CMD`), and **only** those.

The bug this prevents: npm installs a command twice into one directory - a POSIX shell script under
the bare name, and the `.cmd` that Windows actually runs. A search that stops at the first existing
file finds the extension-less `npx`, hands it to `CreateProcess`, and gets *"The specified
executable is not a valid application for this OS platform"*. The platform and the extension list
are **parameters**, not ambient reads, so the machine this matters on need not be the machine
running the test (`LanguageServerLookupTests.cs`).

## PythonEnvironment

### Why an interpreter at all

The files under review live in a detached worktree of one commit. A virtual environment is not
committed, so it is never in there. But an interpreter path is just a path: it can point into the
reader's own clone while the analysed files sit elsewhere. That is what makes a review see the
project's dependencies.

### Resolution order - `Candidates(repoPath)` (`:50`)

Asked of the **repository**, not the worktree:

1. `STAMPEDED_PYTHON_PATH`
2. an active `VIRTUAL_ENV` -> `<root>/{bin/python | Scripts/python.exe}`
3. an active `CONDA_PREFIX` -> the same layout
4. `.venv`, then `venv`, then `env` inside the repository
5. `python3`, then `python` on PATH (via `LanguageServers.OnPath`)

`InterpreterFor` (`:23`) takes the first candidate that is non-null **and exists**, logs
`interpreter: <path> (<reason>)` and - the part that matters on someone else's machine - logs every
candidate that lost and why (`not set`, `<path> does not exist`). The question is never "which did
it pick" but "why not mine".

### Why the interpreter is offered twice

Servers disagree about where the interpreter is named, and a client cannot know which a given server
reads, so it is supplied everywhere any server we might start looks.

**At initialize** - `InitializationOptions(interpreter)` (`:98`):

```csharp
new { python = new { pythonPath = interpreter, defaultInterpreterPath = interpreter },
      workspace = new { environmentPath = interpreter } }   // jedi-language-server's name for it
```

**Via `workspace/configuration`** - `SettingsFor(section, interpreter)` (`:78`):

- `"python"` -> `{ pythonPath, defaultInterpreterPath, analysis }` (the analysis block is nested here
  *as well as* standing alone, because servers and versions differ on whether it is one section or
  two)
- `"python.analysis"` / `"basedpyright.analysis"` -> `Analysis`
- anything else -> `{}`

`Analysis` (`:91`) is `{ autoSearchPaths = true, useLibraryCodeForTypes = true, logLevel = Tracing ?
"Trace" : "Information" }`. Under trace, pyright reports the interpreter it settled on and every path
it searches for imports into the Log pane - the whole answer to "why is this import unresolved on
that machine". Both are passed together at every start site (`ReviewWorkspace.cs:2115, 2127`).

### pyrightconfig / pyproject

There is **no code** that reads `pyrightconfig.json` or `[tool.pyright]`. That is the design, not an
omission: the project's config is committed, so the worktree has it and the server reads it itself.
The hazard is that its relative `venvPath` resolves against the checkout, where there is no
environment. The contract relied on - and asserted by `PythonProjectConfigTests.cs` - is that **the
interpreter supplied via initialize and configuration wins over a project's `venvPath`/`venv`**. The
test builds a worktree with `[tool.pyright] venvPath="." venv=".venv"` (pointing at nothing) plus a
separate clone whose `.venv` holds `mylib`, and asserts that go-to-definition on `mylib.hello` still
lands in `mylib`.

## Stampeded.RoslynLsp

### Program

```csharp
var protocol = Console.OpenStandardOutput();
Console.SetOut(Console.Error);              // stdout is the protocol from here on
MSBuildLocator.RegisterDefaults();          // BEFORE any Roslyn assembly loads
Environment.SetEnvironmentVariable("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");
```

Everything written for a human - including the workspace's own load log - goes to stderr, which the
client copies into its Log pane. `--version` prints a banner and exits 0. Exceptions out of
`RunAsync` are logged and exit 1.

### RoslynLspServer

One `RoslynWorkspaceService head`, one nullable `@base`, one `Task headLoad`. Requests are handled
**serially, in arrival order** (`RunAsync`, `:44`) - Roslyn answers are cheap once loaded, and a
review asks one question at a time. Any handler exception is logged as `<method> FAILED: ...` and
answered with a `null` result.

| Method | Handler |
| --- | --- |
| `initialize` | `:131` |
| `shutdown` | `:121` (sets `shuttingDown`) |
| `textDocument/definition` | `:315` |
| `textDocument/references` | `:325` |
| `textDocument/hover` | `:336` - returns `{contents:{kind:"plaintext", value}}` |
| `textDocument/documentHighlight` | `:344` - kind 3 for definitions, 2 for references |
| `textDocument/documentSymbol` | `:420` |
| `textDocument/semanticTokens/full` | `:480` |
| `textDocument/prepareCallHierarchy` | `:360` |
| `callHierarchy/incomingCalls` / `outgoingCalls` | `:384` |
| `workspace/symbol` | `:532` (capped at 100, head only) |
| `stampeded/loadBase` | `:220` |
| `stampeded/decompileTarget` | `:567` |
| `stampeded/changedMembers` | `:544` |
| `stampeded/memberDisplays` | `:557` |
| notifications: `exit`, `textDocument/didOpen`, `textDocument/didChange`, `initialized`, `$/cancelRequest` | `:99` |

Anything else returns `null`.

**`initialize`** (`:131`) advertises `textDocumentSync: 1` (full text), the definition, references,
hover, documentHighlight, documentSymbol, workspaceSymbol and callHierarchy providers, a
`semanticTokensProvider` with the 12-name legend at `:509`, and `experimental: { decompileTarget,
derivedBaseSide, changedMembers, loadsAsynchronously }`. The solution comes from
`initializationOptions.solution`. **The load is started on a background task and `initialize`
answers immediately** - loading is minutes on a large solution - and the client learns readiness
through `stampeded/state` notifications pushed from `head.StateChanged` (`ReportState`, `:212`).

**`WatchParent`** (`:182`) polls the client's `processId` every 3 seconds and calls
`Environment.Exit(0)` when it is gone. A client killed with a signal never sends `shutdown`, and an
invisible language server is a solution's worth of memory left behind.

**Base-side derivation - `stampeded/loadBase`** (`:220`):

```jsonc
{ "replaced": {relPath: text}, "removed": [relPath], "added": {relPath: text} }
```

It first awaits `headLoad` - deriving from a solution still being opened produces a workspace that
knows nothing - then `new RoslynWorkspaceService().LoadFrom(head, replaced, removed, added)`,
disposes any previous `@base`, and logs the counts. The client sends exactly the three maps
`BaseSideTextsAsync` produced.

**Side routing - `Target(uri)`** (`:285`): `LspUri.ToPath`, then `parsed.Query.Contains("side=base")`
picks `@base` over `head`; `service.ToRelativePath(path)` gives the file. `UriOf` (`:607`) adds
`?side=base` back when the service *is* `@base` (reference equality).

**`didOpen`/`didChange` -> `OverlayAsync`** (`:251`) takes `params.text` or
`contentChanges[0].text` and applies it as a `SetTextOverlay` **only if it differs** from what the
workspace already has (line endings normalised). Re-stating the file on disk would throw away the
compilation that already knows it.

**Document symbols** (`:420`) are computed by `DocumentOutline.Compute` on whatever text the
workspace holds - a pure function, so it answers loaded solution or not. `NameOf` (`:451`) strips the
leading keyword from an outline title (`"class Greeter"` -> `"Greeter"`), because the protocol
carries the kind in its own field and the client joins names with dots to build the display the
change map is compared against; leaving the keyword in would poison that comparison. `SymbolKindOf`
(`:458`) maps both the outline's C# keywords *and* Roslyn's own `DeclarationHit` kind names into
`SymbolKind` numbers.

**Semantic tokens** (`:480`) re-encode `SemanticToken`s into the relative 5-tuple stream, mapping
Roslyn classification names to the legend via `LegendNameOf` (`:516`); classifications with no legend
name are dropped.

**`prepareCallHierarchy`** (`:360`) converts the offset back to (line, column) with `LineColumnAsync`
(`:586` - an O(n) character scan) and resolves via `GetSymbolOnLineAsync`, then reports the
**symbol's own position** as both `range` and `selectionRange`, because incoming and outgoing calls
re-resolve from that item and it must name the member, not the call site that led there.

Tests: `RoslynLspServerTests.cs` drives a real server through `LspSemanticProvider`.

## DecompilationService

`Decompilation/DecompilationService.cs`. **Shells out to nothing** - it is an in-process ILSpy
(`ICSharpCode.Decompiler`) call, the one place in the tool where an external process is not involved.

```csharp
public static DecompiledType DecompileType(string assemblyPath, string reflectionName, int targetMetadataToken)
```

- `DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false }` - a review's
  assembly graph is rarely complete.
- `new CSharpDecompiler(assemblyPath, settings).DecompileType(new FullTypeName(reflectionName))`,
  where `reflectionName` is e.g. ``System.Collections.Generic.List`1``.
- Output is written through `CSharpOutputVisitor` with Allman formatting into a `StringWriter`,
  wrapped by `MemberLocatingTokenWriter` (`:43`).

**`MemberLocatingTokenWriter`** watches the token stream and records the line of the declaration
whose `IEntity` has `MetadataTokens.GetToken(entity.MetadataToken) == targetToken`.
`WriteIdentifier` gives the exact line (fields and events put the name inside a
`VariableInitializer`, so it climbs one parent); `StartNode` on an `EntityDeclaration` is the
fallback for declarations that write no `Identifier` at all (indexers, operators), whose start may
point at leading documentation or attributes. `FoundLine => identifierLine ?? declarationLine`, and
the caller defaults to line 1.

The line comes from `ILocatable.Location` - **the writer that produces the text** - because comments
and preprocessor directives end their own line without going through `NewLine()`, so counting
`NewLine` calls drifts further off with every doc comment above the member.

### How a sourceless definition becomes a read-only document

`ReviewWorkspace.NavigateToDefinitionAsync` (`:2194`):

1. `GetDefinitionAsync` returns null -> `OpenDecompiledDefinitionAsync` (`:2259`):
   `sem is IDecompileTargets` -> `GetDecompileTargetAsync` -> `DecompilationService.DecompileType` on
   a background thread -> `DiffDocumentViewModel.ForSource(TypeName + ".cs", text)` with the title
   `<Type> [decompiled]`, document id `decomp:<ReflectionName>`, caret at `result.MemberLine`.
   Failure surfaces as a status message *and* a log line.
2. `GetDefinitionAsync` returns a path **outside the tree** (`ToRelativePath` gives null) ->
   `OpenDefinitionOutsideTheTree` (`:2225`): reads the file, opens it as `source:<path>`, tab tooltip
   is the full path. This is what makes F12 into a Python package in the environment work at all; it
   used to do nothing.

Both routes produce an ordinary source document with no diff behind it, which is what makes them
read-only in practice. Both `RecordOrigin` and `history.Record`, so Back works.

## Testing

### TestService

```csharp
public sealed class TestService(string worktreePath)
public async Task<(int ExitCode, IReadOnlyList<TestResult> Results)> RunAsync(
    string argsLine, Action<string> onOutputLine, CancellationToken ct, string? coverageOutput = null)
```

- `started = UtcNow - 5s` is the cutoff for "a TRX this run produced".
- `argsLine.Split(' ')` - no quote handling, marked with an explicit `// ponytail:` comment at `:16`
  acknowledging the ceiling (the args box is a developer-facing escape hatch).
- Without coverage: `dotnet <args>`. With coverage: `dotnet-coverage collect --output <file>
  --output-format cobertura -- dotnet <args>` - Microsoft's dynamic engine wraps the whole run, so no
  project changes are needed.
- Environment `OPENSSL_ENABLE_SHA1_SIGNATURES=1` plus `StripMsBuildLocatorVariables`;
  `CommandResultValidation.None`; both stdout and stderr piped line by line into `onOutputLine`;
  start and exit code logged to `CliLog`.
- Collection: every `*.trx` below the worktree whose `LastWriteTimeUtc >= started`, parsed;
  `XmlException` swallowed (a truncated TRX from an aborted run is not worth failing over).
- `ParseDirectory(directory)` (`:56`) does the same without the timestamp filter, for downloaded CI
  artifacts.

Call sites: `Panes/TestsPaneViewModel.cs:212/217` (A/B) and `:291` (single run). Both **wait for the
semantic load to finish first** (`:201`, `:284`) - the semantic load runs `dotnet restore` and
design-time builds in the same worktree, and a concurrent test build trips over half-written `obj/`
state and dies with an opaque "Build failed" (the test platform hides MSBuild's errors).

### TrxParser

`TestOutcome { Passed, Failed, Skipped, Other }`.

`TestResult(TestName, Outcome, Duration, ErrorMessage?, StackTrace?)` with `TryGetSourceLocation()`
(`:25`) - the first `" in <file>:line <n>"` frame, via
`[GeneratedRegex(@" in (?<file>.+?):line (?<line>\d+)")]`. That is what makes double-clicking a
failure jump to the frame.

`Parse(trxContent)` (`:41`): `XDocument`, namespace
`http://microsoft.com/schemas/VisualStudio/TeamTest/2010`, every `UnitTestResult` descendant;
`outcome` mapped with `"NotExecuted"` and `"Skipped"` both to `Skipped` and anything unrecognised to
`Other`; `TimeSpan.TryParse` on `duration` (a failure silently leaves `default`); message and stack
from `Output/ErrorInfo/{Message,StackTrace}`.

### CoberturaParser

```csharp
public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Parse(string xml, string rootPath)
```

Every `<class>`: the `filename` attribute, resolved by `Resolve` (`:38`) against, in order, the
absolute path itself, each `<source>` element, or `rootPath`; the first candidate landing **strictly
inside** `root` wins and is returned root-relative with `/` separators. Line hits are merged **by
max** across class entries sharing a file (partial classes, multiple targets). The result is
consumed by `ReviewWorkspace.SetCoverage` for the gutter overlay.

### TestRunComparison

The question a text diff of two outputs cannot answer: *did this change introduce the failure, or
was it already broken at base?*

```csharp
public sealed record TestRunComparison(
    IReadOnlyList<TestResult> NewlyFailing, IReadOnlyList<TestResult> Fixed, IReadOnlyList<TestResult> StillFailing,
    int BasePassed, int BaseFailed, int HeadPassed, int HeadFailed)
```

`Compare(baseResults, headResults)` (`:14`) keys on **test name** (ordinal), because one name can
appear once per target framework and one failing result marks the name failing:

- head failures: in `baseFailed` -> `StillFailing`, else -> `NewlyFailing` (a newly written test
  that fails counts as a new failure - deliberate, `:29`);
- head passes that were failing at base and are not also failing at head -> `Fixed`;
- the four counts are distinct-name counts, not result counts.

### GeneratedSources

Generated code is not in git, so a change that is entirely about what a generator emits is invisible
in the diff. This turns it into an ordinary before/after comparison.

- `EmitProperty = "-p:EmitCompilerGeneratedFiles=true"` (`:20`); output lands under
  `obj/<config>/<tfm>/generated/`.
- **`BuildAsync(worktreePath, chosenSolution, ct)`** (`:27`): `dotnet build [<solution>]
  -p:EmitCompilerGeneratedFiles=true --nologo -v quiet -p:GenerateDocumentationFile=false`. The
  solution is **named** via `SolutionTarget.ForRoot` - a root with several solutions is refused
  outright by `dotnet` ("Specify which project or solution file to use"), which is exactly why
  generated sources never arrived for repositories shipping an installer or extension solution
  beside the product's own. The log line is written *before* the build, because a command is
  normally logged when it finishes and this one runs for minutes.
- **`Collect(worktreePath)`** (`:56`): every directory named `generated`, keyed by `<project-relative
  prefix>/generated/<tail>`. `RelativeKeyPrefix` (`:79`) accepts only the real layout -
  `.../obj/<config>/<tfm>/generated`, i.e. `parts.Length - lastIndexOf("obj") == 4` - so a source
  directory that happens to be called `generated` is ignored. The config and TFM are dropped from the
  key so the two sides pair up even when built differently; the project stays in the key because two
  projects can host the same generator.
- **`DiffAsync(baseWorktree, headWorktree, ct)`** (`:95`): the union of both key sets, ordered; kind
  from which side has the file; hunks from `DiffFilesAsync`; identical files produce no hunks and are
  omitted entirely. Each `FileDiff` carries `new GeneratedSource(oldFile, newFile)`.
- **`DiffFilesAsync`** (`:123`): `git diff -U3 --no-index -- <old|/dev/null> <new|/dev/null>` run
  from `Path.GetTempPath()`, with `okExitCodes: [1]` because `--no-index` reports "differences found"
  as exit 1. Parsed with the review's own `GitDiffParser` so the output has the same shape as
  everything else; the parsed paths are discarded (they would be absolute paths into two throwaway
  worktrees).

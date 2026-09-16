# The Avalonia UI layer

`src/Stampeded/` - the app project. Roughly 24k lines across 158 files.

| Directory | What lives there |
| --- | --- |
| `*.cs` (root) | startup (`Program`, `App`), shell (`MainWindow`, `MainViewModel`), the session hub (`ReviewWorkspace`, `ReviewScopes`, `ReviewComments`), dialogs, preference/state singletons, `ScreenshotWatcher`, `Images`, `ViewLocator` |
| `Docking/` | `StampededDockFactory` - the one place the layout is built |
| `Documents/` | Dock `Document` view models + views: diff (unified), side-by-side, overview, review verdict, start page, plain text; comment-thread rendering; the shared gesture table |
| `Panes/` | Dock `Tool` view models + views |
| `Editor/` | AvaloniaEdit extension points |
| `Diff/` | diff chrome: margins, row background renderer, overview scrollbar, context gaps, folding glue, classification colours |
| `Controls/` | small custom controls |
| `Controls/TreeView/` | **vendored from ILSpy** (MIT), kept close to upstream - read `src/Stampeded.Core/TreeView/README.md` first. The model half lives in `Stampeded.Core/TreeView/`. Used instead of Avalonia's `TreeView` because `TreeFlattener` projects the hierarchy into one virtualized `IList`: depth costs an indent value, not a nested container, which is what makes an unbounded call hierarchy survivable. |
| `Themes/` | `ThemeManager`, `SyntaxColor`, `SyntaxColorPalettes` |
| `Navigation/` | `NavigationHistory<T>` (also vendored from ILSpy) |

The five largest files: `ReviewWorkspace.cs` (2802), `DiffDocumentView.axaml.cs` (1270),
`StartDocumentViewModel.cs` (1049), `SideBySideDocumentView.axaml.cs` (684), `ReviewScopes.cs` (653).

## Startup

`Program.Main` and `MainViewModel`'s construction order are described in
[review-session.md](review-session.md). What belongs here is the rest of the shell.

### App.axaml.cs

`App.Workspace` is a **static mutable `ReviewWorkspace?`** - the single review session of this
process. Nearly every view reaches it as `App.Workspace?....`.

`OnFrameworkInitializationCompleted` creates `MainWindow`, hooks `desktop.ShutdownRequested ->
Workspace?.Shutdown()`, and registers `PosixSignalRegistration` for SIGTERM/SIGINT/SIGHUP, each
calling `Workspace?.Shutdown()` **without cancelling the signal** - a language server is a child
process that outlives an unclean exit, and holding the process open to tidy is how a kill becomes a
`kill -9`. The registrations are kept in a static list so they stay alive.

`OpenRepositoryAsync(path, prNumber)` validates `.git` exists, shuts the old workspace down,
rewrites `Program.RepoPath` / `Program.Host`, then **replaces `window.DataContext` with a new
`MainViewModel`** - which is what rebuilds the entire dock.

`OpenFromUrlAsync(input)` parses Azure DevOps URLs **first** (GitHub's grammar accepts bare
`owner/repo` and would read `dev.azure.com/org/...` as a repo owned by `dev.azure.com`), then
GitHub. It searches `Program.RepoPath` + `RecentRepos` for a clone whose **any** remote matches, and
otherwise asks where to clone and makes a blobless partial clone (`--filter=blob:none`).

`App.NextFolderAnswer` is a test seam: the folder picker is the desktop portal's own dialog and
nothing in-process can drive it, so the screenshot harness pre-answers the next question.

### App.axaml - resources and styles

- Theme dictionaries `Light`/`Dark` define `Stampeded.EditorBackground`,
  `Stampeded.EditorSelectionBrush`, `Stampeded.ChromeBackground`, `Stampeded.TreeFocusFill/Border`,
  **and restate the whole Simple-theme palette**. Only the *brushes* are overridden, not the colours
  behind them: the Simple theme builds each brush from its colour with a `StaticResource`, resolved
  once at parse time.
- `Button.tool` is the flat icon-button class used by every pane toolbar; `Border.toolsep` is the
  hairline group separator.
- `DocumentTabStripItem` binds `ToolTip.Tip` to `TabTooltip` **by ReflectionBinding** - the strip's
  item is typed as a dockable and only file documents carry the property.
- `OverlayPopupHost` gets `RenderTransform = {x:Static local:ZoomState.PopupScale}` - popups live in
  the window's overlay layer, outside the `LayoutTransformControl` that scales the content, so they
  are scaled separately.
- The empty `<NativeMenu.Menu><NativeMenu /></NativeMenu.Menu>` suppresses the "About Avalonia" app
  menu macOS would otherwise synthesize.

### MainWindow

`LayoutTransformControl` (ScaleTransform bound to `MainViewModel.Zoom`) -> `DockPanel` painted
`Stampeded.ChromeBackground` -> `NativeMenuBar` (top), busy bar (bottom), and a `Panel` holding
`dock:DockControl` plus the **preparation overlay** - a modal scrim bound to
`StartPage.State.IsPreparing` listing `StartPage.PrepareItems` with a `WaveSpinner` per pending item
and a "Continue now" button.

`LayoutTransformControl` rather than a `RenderTransform` is deliberate: it re-runs layout at the new
scale, so text is laid out *and rendered* at that size instead of a fixed layout being magnified.

## The docking model

`StampededDockFactory` extends `Dock.Model.Mvvm.Factory`. There is **no registry indirection** - the
pane set is small and closed.

```
Root (IRootDock)
+- mainLayout: ProportionalDock, Horizontal
   +- leftDock: ProportionalDock (Proportion 0.2, Vertical)
   |  \- filesDock: ToolDock "FilesDock", Alignment.Left
   |     \- Explorer*, Structure, Map                      (* active)
   +- ProportionalDockSplitter
   \- rightSide: ProportionalDock, Vertical
      +- Documents: DocumentDock "Documents", IsCollapsable = false
      +- ProportionalDockSplitter
      \- bottomDock: ToolDock "BottomDock", Alignment.Bottom, Proportion 0.28
         \- References*, CallGraph, Comments, Commits, History,
            Checks, MergeQueue, Tests, Run, Log
```

Pane ids: `Explorer`, `Map`, `Structure`, `References`, `CallGraph`, `Comments`, `Commits`,
`History`, `Checks`, `MergeQueue`, `Tests`, `Run`, `Log`.

Each pane is recorded in `panes: Dictionary<string, (Tool Pane, ToolDock Home)>`:

```csharp
public T? Pane<T>(string id) where T : Tool
public void ShowPane(string id)
```

`ShowPane` re-adds the pane to its **home** dock when it is nowhere in the layout (`FindDockable`
walks `RootDock` and every floating `Window.Layout`), then activates and focuses it.
`workspace.Comments.Pane` is wired here so `ReviewComments.BeginComment` can activate the pane.

### Documents

Documents are **not** created by the factory. They are created on demand by `ReviewWorkspace`
through one private `ShowDocument<T>(id, create)` helper. The id vocabulary is listed in
[review-session.md](review-session.md).

**A file is one tab in either layout.** `ShowDiffDocument` keys both layouts on `diff:<path>`; if
the existing document's type does not match `DiffLayoutPreference.SideBySide`, it is closed and
rebuilt.

### Layout persistence - there is none

`CreateLayout()` runs fresh on every `MainViewModel` construction. The only window-level state that
survives a session is the window geometry (`WindowPlacement`), the zoom (`ZoomPreference`), the tab
row mode (`TabRowsPreference`) and the diff layout (`DiffLayoutPreference`). Anyone adding layout
persistence would hook the factory's serializer around `MainViewModel.cs:146`.

### ViewLocator

An explicit `Dictionary<Type, Func<Control>>` mapping 22 view models to views, installed as
`Window.DataTemplates`. A missing entry renders `"No view registered for X"` rather than throwing.
**Any new document or pane needs an entry here or it renders as that text block.**

## Documents

### Common contracts

```csharp
public interface IDiffDocument
{
    FileDiff File { get; }
    string? Id { get; }
    void RequestCaret(int blobLine, bool oldSide = false);
}

[Flags] public enum ReviewCommands {
    None, JumpToHunk, JumpToUncovered, ToggleBlame, CommentAtCaret, GoToDefinition,
    FindReferences, HighlightOccurrences, ShowCallGraph, HistoryOfSelection, DebugHere }

public interface IReviewDocumentView
{
    ReviewCommands Supported { get; }
    string DocumentId { get; }
    (int BlobLine, bool OldSide)? CaretOrigin { get; }
    bool JumpToHunkCommand(int direction);
    void JumpToEdgeHunk(int direction);
    void JumpToUncoveredCommand();
    bool BlameVisible { get; }
    void ToggleBlameCommand();
    void CommentAtCaretCommand();
    void GoToDefinitionCommand();
    // ... FindReferences, HighlightOccurrences, ShowCallGraph, HistoryOfSelection, DebugHere
}
```

`IReviewDocumentView` exists so the two layouts cannot drift silently: adding a command forces both
views to answer. `ReviewViews` is a `Dictionary<string, IReviewDocumentView>` keyed by **dockable
id**, and `ReviewViews.Active` resolves through `Documents.ActiveDockable.Id`, **not through focus** -
clicking a tab header need not move focus, and a command landing on a document nobody is looking at
is worse than one that does nothing.

`DiffDocumentView.Supported` is every flag. `SideBySideDocumentView.Supported` is
`JumpToHunk | GoToDefinition | FindReferences | CommentAtCaret` only; everything else routes to
`NotHere(string)`, which logs and posts a status line.

### DiffDocumentViewModel

Key members: `PristineModel` (the diff as built from blobs, the base every comment-thread re-splice
starts from), `Model` / `ReplaceModel`, `IsSourceView`, `Historical` + `HistoricalSha`, `IsPatch`,
`TabTooltip` / `TabTooltipOverride`, `RequestCaret` / `TakePendingCaret` (the pending-caret pair
exists because navigation may open a document and *then* say where to land, in either order), and
the static `ForSource(relPath, text)` factory which builds an identity diff.

### DiffDocumentView - the unified diff

Composition, in the constructor:

| What is installed | Where |
| --- | --- |
| `SearchPanel.Install(Editor)` | AvaloniaEdit's find bar |
| `DiffLineBackgroundRenderer(() => model?.Tags)` | `TextView.BackgroundRenderers` |
| `TextMarkerService` | `TextView.BackgroundRenderers` (occurrence highlights) |
| `ThreadElementGenerator` + `CommentThreadBox` | `TextView.ElementGenerators` |
| `ReferenceElementGenerator` | `TextView.ElementGenerators` |
| `DiffLineNumberMargin` | `TextArea.LeftMargins.Insert(0, ...)` |
| `FoldViewportAnchor.Install(Editor)` | tunnelling pointer handler on `TextArea` |
| `ContextGapView(Editor)` | element generator + background renderer + `LayoutUpdated` |
| `PointerCrossHairRenderer` (DEBUG) | `TextView.BackgroundRenderers` |
| `BlameMargin` / `CoverageMargin` | inserted and removed on demand |

Handler routing subtleties, each of which cost a debugging session:

- editor gestures are handled **Tunnel** on `TextArea` because AvaloniaEdit has its own bindings for
  Ctrl+Down/Up and a key it acts on never reaches the window;
- `CommentBox` keydown is `Bubble, handledEventsToo: true` because a `TextBox` with `AcceptsReturn`
  handles Enter itself before any handler declared on it;
- pointer **release** is handled on `TextArea`, not `TextView`, because AvaloniaEdit captures the
  pointer and captured releases are raised on the capturing control.

`ActiveView` / `ActiveViewChanged` is the static view registry the Explorer, Structure and History
panes follow. `ViewFor(vm)` uses a `ConditionalWeakTable<DiffDocumentViewModel, DiffDocumentView>`
because `ActiveView` is stale the moment a tab is selected without the mouse.

Rendering a model:

```
Editor.Text = model.Text
ApplySyntaxColors()                 // TextMate paint, sliced
margin.Columns = IsSourceView ? New : Both;  margin.Tags = model.Tags
Overview.Attach(Editor, model.Tags)
InstallFoldsAndGaps(model)
ApplyMarginCursors()
referenceGenerator.References = null;  markers.RemoveAll(_ => true)
QueueSemanticsRefresh()
```

**Folding vs context gaps.** Two *separate* mechanisms, deliberately not nested:

- **Structural folds** (types, members, `#region`) come from `ISemanticProvider.GetFoldRegionsAsync`
  over **one side's text**, mapped to document lines through `DiffFolding.Members(regions,
  sideToDocLine)` and installed via `FoldingManager`. When the provider answers synchronously
  (`Task.IsCompletedSuccessfully`) they are installed immediately; a language server's answer
  re-installs later.
- **Context gaps** (unchanged runs) are hidden by `ContextGapView` using `TextView.CollapseLines`
  directly. `RefreshFoldings` clips structural ranges to what is visible so the fold margin never
  offers to collapse code the reader cannot see.

**Blame** asks each side separately so one unblameable side does not kill the other; `oldRev` is
`null` in the since-last-pass scope, because that scope's base is a *tree*, not a commit, and `git
blame` answers a tree with "Non commit".

**Hover** is a 400 ms `DispatcherTimer`, gated by `HoverPointer.PointsElsewhere` (text position, not
pixels). Every empty outcome is logged once per reason through `HoverLog` - "no tooltip" and "no
hover" are otherwise indistinguishable.

**Semantic layer**: `RefreshSemanticsAsync` asks each side for tokens (falling back to
`GetSemanticTokensForTextAsync` when the loaded workspace holds a different revision than what is
displayed), builds a `RichTextModel` + `TextSegmentCollection<ReferenceSegment>`, and installs a
fresh `RichTextColorizer` as a `LineTransformer`.

### SideBySideDocumentView and SideBySidePane

Two `ReviewTextEditor`s with a `GridSplitter`, fed from `SideBySideModel`. **Equal line counts on
both sides** (Filler rows on the shorter one) are the invariant that makes everything else work.

- **Scroll sync**: the `ScrollViewer`s only exist once templates are applied, so wiring retries up to
  20 times at `DispatcherPriority.Background`. `Sync` copies `Offset` with a re-entrancy guard
  released **a dispatcher turn later** - a narrower pane clamps the offset it was given and reports
  the clamp as a scroll of its own from inside the layout pass, and answering that echo makes the two
  panes correct each other until layout gives up.
- **Fold mirroring**: both panes get the *same* ranges, so sections are matched by index.
- **One gap view drives both editors**: `new ContextGapView(Left, Right)`.
- **Thread rows**: a thread belongs to one side, so the other pane draws a `ThreadSpacer` of exactly
  the same height.
- `SideBySidePane` carries the per-pane semantic/navigation layer. A pane holds exactly one blob, so
  its `blobToDocLine` mapping is a dictionary lookup rather than the tag disambiguation the unified
  view needs.

### OverviewDocumentViewModel

The review brief. `CanClose = false` - it is the review's home tab.

Sections, each an `Expander` over a `ListBox` with `VerticalScrollBarVisibility="Disabled"` (one
scroll region for the page): Description (`MarkdownScrollViewer`), Commits, Changed files
(cost/churn), Changed members.

Above them, docked and non-scrolling: title, scope toolbar, scope banner painted with `ScopePalette`,
warning lines (`LocalHeadLine`, `OfflineLine`, `WorkingTreeLine`, `ToolStatus`), estimate, CI header
+ failing checks, reviewers, coverage, tests, linked issues.

Each rebuild method is bound to one workspace event. `RebuildLinkedIssuesAsync` puts every `#123` in
the body **to the host** and shows only the ones something answers to, with a pass counter to drop
overtaken runs.

The view installs `MarkdownLinks.NewEngine()` so description links open, enables `MarkdownSelection`,
and posts `MarkdownEmphasis.Repair` after every markdown re-render. It is `Focusable = true` so `o`
has somewhere to land.

### ReviewDocumentViewModel

Every comment of the review, each quoted with ±3 lines of code read from the blob (with a
`(rev, path) -> string[]` cache), plus the verdict row and the merge block.

`RefreshMergeAsync` reads the merge state fresh on every rebuild - it changes with every push and
every review. `MergeButtonTip` recomputes from `MergeExplanation` because a disabled button shows no
tooltip, so the explanation is also put on the status *line*. `Submit` keeps `Outcome` separate from
`Status` because posting a review reloads the comments, and the reload used to overwrite the answer.

### StartDocumentViewModel

Three columns: recent repositories, open pull requests, branches/stashes. Each column has a filter
toggle that takes over the column header, reachable by Ctrl+F or by just typing into the list.

Branch annotation joins branches against PRs (excluding fork heads - a fork's `master` is a different
branch), computes merge state from `ListMergedBranchesAsync` plus a *cached, background*
patch-equivalence check (keyed by branch tip, invalidated whenever the default base moves), sync
state against the PR head, worktree ownership and ahead-counts.

Also here: the `in progress` banner with Resolve / Continue / Skip / Abort, driven by
`Git.ListInProgressAsync`, re-asked on every window activation - a conflicted rebase is usually
finished in a terminal.

The preparation checklist has eight fixed rows. **Only row 0 (the diff) gates the overlay**:
semantics, map, CI, churn and comments arrive into a window already in use.

A deliberate warm-up: `Task.Run(() => SyntaxPainter.For("warm.cs")?.Paint(...))` pays the TextMate
registry and first-tokenizer cost (~¼ s) while the start page waits for a click.

## AvaloniaEdit extension points

### ReviewTextEditor

Three things only, each load-bearing:

```csharp
protected override Type StyleKeyOverride => typeof(TextEditor);
```

Without it Avalonia resolves the template by runtime type, AvaloniaEdit's template never applies,
**no `ScrollViewer` is installed**, scroll offsets stay 0 and `Copy` cannot reach the `TextArea`.

Font `"Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace"` at 13; `SelectionCornerRadius = 0`
and a flat translucent `SelectionBrush` so **selected text keeps its syntax colours**;
`ThemeManager.ThemeChanged -> TextView.Redraw()` on attach/detach, because painted lines cache their
colour decisions.

### Element generators

| Generator | Replaces | Invalidated by |
| --- | --- | --- |
| `ReferenceElementGenerator` | spans in a `TextSegmentCollection<ReferenceSegment>` -> `VisualLineReferenceText` (clamped to the line; hyperlinks cannot span line breaks) | setting `.References` + `Redraw()` |
| `ThreadElementGenerator` | the `ThreadMarkerPrefix...Suffix` marker text on a synthetic line -> `InlineObjectElement(markerLength, control)` | document re-splice, or `Redraw()` when only the content changed |
| `ContextGapElementGenerator` | the whole run of hidden lines -> one `InlineObjectElement` carrying the reveal buttons | `ContextGapView.Apply()` |

`ContextGapElementGenerator.ConstructElement` spans **every line the gap hides**, not just the one
the bar sits on: a visual line may cover several document lines only while an element accounts for
their text, and a collapsed line cannot start a visual line of its own.

`VisualLineReferenceText.OnQueryCursor` deliberately **does not** set `e.Handled` - marking it
handled suppresses `PointerHoverLogic`'s tracking and hover events then fire with stale args.

### Background renderers

| Renderer | Layer | Draws |
| --- | --- | --- |
| `DiffLineBackgroundRenderer` | `Background` | full-width added/removed/filler row tints + intra-line word-diff spans from `tag.WordDiffs` |
| `ContextGapBackgroundRenderer` | `Background` | the gap row band under the bar |
| `TextMarkerService` | `Selection` (behind selection) | rounded background rects for occurrence highlights |
| `LineHighlightAdorner` | `Selection` | one-shot amber line flash, 800 ms |
| `CaretHighlightAdorner` | `Caret` | one-shot rectangle around the caret; rects kept in **document** coordinates and translated by live `ScrollOffset` each frame |
| `PointerCrossHairRenderer` (DEBUG) | `Caret` | crosshair + `(x, y) Ln/Col` readout |

`CaretHighlightAdorner.InvalidateHostLayer` and `PointerCrossHairRenderer.UpdatePointer` both loop
over `textView.Layers` calling `InvalidateVisual()`: **`TextView.InvalidateLayer` only invalidates
the TextView's own measure in AvaloniaEdit 12** and never re-renders the per-layer child controls.

### Margins

| Margin | Width | Draws |
| --- | --- | --- |
| `DiffLineNumberMargin` | `ColumnCount * (digits*digitWidth + 8) + 3 + 4` | old and/or new blob line numbers, a 3 px added/removed strip at the right edge, and a **drawn vertical ellipsis** on a context-gap row (the gutter's mono font is not guaranteed to carry `⋮`) |
| `BlameMargin` | `charWidth * 25 + 8` | age-tinted `sha7 author age` rows, text only on the first row of a same-commit run |
| `CoverageMargin` | 5 px | 3 px green/red strip per measured head line |

All three begin `Render` with `ContextGapChrome.DrawRows(...)` - the gap bar is an inline object and
can only cover the *text*, so each gutter paints its share of the band.
`ContextGapFoldingMargin` is a `FoldingMargin` subclass doing the same, installed **in place of** the
margin `FoldingManager.Install` added; otherwise the band has a notch in it.

### Line transformers

Two `RichTextColorizer`s are stacked per editor: **syntax** inserted at index 0, **semantic**
appended. Both are removed and rebuilt rather than mutated. `ClassificationColors` maps Roslyn
`ClassificationTypeNames` to a light/dark hex pair with a `(name, dark)` cache of frozen
`HighlightingColor`s.

### Scroll and anchor helpers

`FoldViewportAnchor`: a tunnelling `PointerPressed` on the `TextArea` captures `(topmost visible
line, its delta from the offset)` **before** the fold margin acts, then restores it at
`DispatcherPriority.Loaded`. `Preserving(editor, action)` is the programmatic form.

Both it and `ContextGapView.RestoreBelow` note the same trap: **`TextEditor.ScrollToVerticalOffset`
is an empty method in AvaloniaEdit 12** (its whole body is a call to `ApplyTemplate`). Everything
that scrolls must reach the editor's `ScrollViewer` through `GetVisualDescendants()`.

## Syntax highlighting

### Why the document cannot simply be highlighted

A grammar is a state machine over *consecutive* lines - a block comment, a `'''` string, a here-doc
all open on one line and close on a later one. A unified diff is consecutive on neither side: it
interleaves two blobs and splices comment rows between them. A removed line that opens a span and
the added line that closes it switch the state on and off in places where neither file does, and
everything below reads in the wrong colour.

### SyntaxPainter

```csharp
readonly record struct ColoredSpan(int Line, int Start, int Length, HighlightingColor Color);
abstract class SyntaxPainter
{
    public abstract IEnumerable<ColoredSpan> Paint(string text);
    public static SyntaxPainter? For(string path, Func<string> content);
    public static SyntaxPainter? For(string path);
}
```

Resolution order: TextMate grammar by extension -> content sniffing
(`GuessFileType.DetectTextType` -> `xml`/`json`) -> the editor's own `.xshd` definitions via
`HighlightingService` (which answers for ILAsm, registered from an embedded resource in its static
constructor). `content` is a lazy `Func<string>` - it is only read when the extension said nothing.

`XshdPainter` runs a `DocumentHighlighter` over a throwaway `TextDocument`. `TextMatePainter`
tokenizes line by line carrying `IStateStack`, with a **100 ms per-line budget** so a pathological
regex leaves a line uncoloured instead of stalling the view.

`TextMateGrammars` holds the registry and theme behind a `Lock`, rebuilt whenever
`ThemeManager.Current.IsDarkTheme` flips (`DarkPlus` / `LightPlus`), with a per-scope painter cache.

### DiffSyntaxColors - the transfer step

Both methods return `IEnumerable<int>` - **the work, not the result** - so the caller decides how
much of it runs before the view draws.

```csharp
public static IEnumerable<int> Build(SyntaxPainter painter, DiffDocumentModel model,
                                     TextDocument document, RichTextModel rich);   // unified
public static IEnumerable<int> Whole(SyntaxPainter painter, TextDocument document,
                                     RichTextModel rich);                          // one pane
```

`AddSide` paints one side's own text, then maps each span's line through
`model.GetSideText(oldSide).sideToDocLine` onto a document row, clamps the length to the row, and
calls `rich.ApplyHighlighting`. The old side is skipped on any row that is not `Removed` - a context
row shows the same text on both sides and is already painted by the new one.

### SlicedPaint

Drives that enumerator in 15 ms slices at `DispatcherPriority.Background`, checking the clock every
128 spans, calling `redraw()` after each slice. The first slice runs *before the view draws*, so the
rows on screen are already coloured; the rest follows between everything else the thread has to do.

Deliberately **on** the UI thread, not off it: a grammar plus its cache is one state machine shared
by every document of that language, and running two at once is a data race.

Both callers cancel the previous paint, remove the old colorizer, install a fresh
`RichTextColorizer` over an **empty** `RichTextModel` and start the paint against it - the model is
read as rows are drawn, so painting it further only needs a redraw.

`QuickInfoView` reuses the same painter for tooltips. Its `Split` separates signature from
documentation, handling both the plain-text convention (blank line) and the markdown-fenced form some
servers return despite being asked for plain text.

## Comment threads and suggestions

```csharp
sealed record ThreadComment(bool IsDraft, string Author, string Body, Guid? DraftId,
    string? ThreadId = null, bool Resolved = false, string? Url = null, long CommentId = 0);
sealed record ThreadData(bool OldSide, int BlobLine, List<ThreadComment> Comments,
    string? OutdatedQuote = null, bool Approximate = false, string? MovedTo = null);

public static Dictionary<string, ThreadData> For(ReviewWorkspace workspace, FileDiff file);
public static List<ThreadAnchor> Anchors(Dictionary<string, ThreadData> threads);
```

Keys are `"n<line>"` / `"o<line>"` for anchored threads and `"od<n>"` for outdated ones (pinned at
the top of the file). Read in `Documents/`, not in a view, because a comment belongs to a line of a
blob - a fact about the review, not about how it is drawn.

How a thread becomes a row:

1. `CommentThreads.For` -> `Anchors` -> `model.WithThreadLines(anchors)` splices a **synthetic
   marker line** per thread into a copy of `PristineModel`.
2. `ThreadElementGenerator` finds the marker text and replaces it with `ControlFactory(key)`.
3. `CommentThreadBox.Build(key, thread)` draws it: OUTDATED/MOVED banner, one header + rendered
   markdown body per comment, then Reply / Resolve / Unresolve / Hide. A resolved thread with no
   draft collapses to a one-line summary with a "Show" button.
4. The box's `Width` is bound to the view's bounds through an observable subscription disposed on
   `DetachedFromVisualTree` - an inline object only sizes to content otherwise.

The box sets `TextElement.FontFamilyProperty = FontFamily.Default` and `Cursor = Arrow` so prose is
not monospace and the editor's I-beam does not bleed over it. Markdown is rendered through the engine
**directly** rather than a `MarkdownScrollViewer`: a `ScrollViewer` inside an editor inline object
would nest scroll regions into every visual line.

**Re-splicing**: when the target text equals the current one, only `Redraw` (the rows are right, only
the content changed). Otherwise the whole model is replaced, and **caret, expanded folds and open
gaps are carried by blob position, not by line number**, because a splice renumbers every line below
it.

**The inline editor** is a `Popup` anchored to the editor. `IsLightDismissEnabled` is turned **off as
soon as the box holds text** so a click aimed at the code behind it cannot take the words with it.
Ctrl+Enter saves, Esc closes. `ScrollToMakeRoomBelow` scrolls the editor far enough that the 150 px
box fits under its anchor - replying to a tall thread otherwise puts the box over the pane below,
since a popup is an overlay and knows nothing of the editor's bounds.

**Suggestions** write ` ```suggestion\n<the line>\n``` \n<existing body>` and place the caret at the
end of the line. Refused on the base side (a suggestion replaces a line of the *new* file) and
refused a second time (the host applies one per comment).

## Keyboard model

Three layers, in the order a key meets them.

### 1. View-level, tunnelling - ReviewGestures

Handled **Tunnel** on the `TextArea` of both layouts because AvaloniaEdit binds Ctrl+Down/Up itself.
Both handlers first bail out if `e.Source` is inside a `TextBox` - the search panel lives inside the
text area.

| Key | Action |
| --- | --- |
| `n` / `Ctrl+Down` | next hunk, **or the next file** when there is none |
| `p` / `Ctrl+Up` | previous hunk |
| `]` / `[` | next / previous file |
| `Ctrl+]` / `Ctrl+[` | next / previous commit in scope |
| `v` | mark viewed and advance |
| `o` | overview / back to file |
| `F12` / `Shift+F12` | definition / references |
| `u` | next uncovered added line |
| `b` | blame margin |
| `c` | comment at caret |
| `Alt+Left` / `Alt+Right` | back / forward |

`StepFileAsync` marks the file being left viewed (forwards only), calls `FinishReadingAsync` off the
last file, and posts `JumpToEdgeHunk(direction)` at `DispatcherPriority.Loaded` so the next file is
entered at the edge the reader is travelling towards.

`OnEditorKeyDown` additionally claims `Escape` (clears occurrence markers) **without marking it
handled** - it is not a review gesture and anything else listening should still hear it.

### 2. Window-level fallback - MainWindow.OnKeyDown

The same table again, plus `F5` (reload), `Ctrl+W` (close tab), `Ctrl++`/`Ctrl+-`/`Ctrl+0` (zoom,
accepting both `OemPlus/OemMinus/D0` and `Add/Subtract/NumPad0`), and `Ctrl+G` (Go to). It runs
**only on what came back unhandled**, so reading gestures work from the Explorer, the Commits pane
and the Tests pane too. Anything whose source has a `TextBox` ancestor is left alone - which is why
the single letters are not `InputGesture`s.

### 3. The NativeMenu

Declared once in `MainWindow.axaml`. On macOS the platform exports it to the system bar; everywhere
else `NativeMenuBar` draws it in the window and hides itself where the platform took it. Three
consequences the code works around:

1. **A `NativeMenuItem` is a model object, not a control** - no `x:Name`, no generated field. The
   items the code-behind needs carry a key in `CommandParameter` and are found once in the
   constructor via `FindMenuItem`.
2. **There is no `ItemsSource`.** `FillRecentMenu` and `FillBuildSolutionMenu` rebuild their
   submenus every time a menu opens.
3. **Gestures are written into the header text**, not set as `Gesture`. On macOS a `Gesture` becomes
   a real AppKit key equivalent that fires *before* the focused text box sees the key - a `v` typed
   into a comment would mark the file viewed.

Menu refresh hooks both `top.Menu.NeedsUpdate` per top-level item **and** `MenuItem.SubmenuOpenedEvent`
bubbling, both landing in `RefreshMenus()`. On macOS the `Exit` item and the separator above it are
removed from the model entirely.

`KeyboardShortcuts.Text` is one raw string constant opened from Help, because single letters never
appear as a gesture next to a command. **Editing a gesture means editing three places**:
`ReviewGestures.Handle`, `MainWindow.OnKeyDown`, and this string (plus the menu header text).

## ScreenshotWatcher - the harness protocol

A 1 s `DispatcherTimer` polling two files: `/tmp/stampeded-screenshot-request` (any instance) and
`/tmp/stampeded-screenshot-request.<pid>` (this instance only, reported in the log at startup). The
own file wins.

**Line 1 is the target PNG path.** Every later line is a command run before the capture. The file is
deleted on pickup. The capture is of the newest visible window, so a modal dialog photographs itself.

| Command | Effect |
| --- | --- |
| `goto:<path>:<line>` | `NavigateToFileLineAsync` |
| `open-file:<relpath>` | `OpenFileAsync` without navigating in |
| `open-range:<base>:<head>` | `OpenLocalRangeAsync` |
| `open-url:<url or owner/repo[/pull/N]>` | `App.OpenFromUrlAsync` |
| `close-review` | `CloseReviewAsync` |
| `pane:<id>` | `Factory.ShowPane(id)` |
| `overview`, `since-last-pass`, `commit-scope`, `commit-next`, `commit-exit` | scope commands |
| `sbs` | toggles `DiffLayoutPreference` |
| `comment`, `callgraph`, `vscode`, `ilspy-fixtures`, `impacted` | the corresponding commands |
| `highlight:<line>:<col>` | `HighlightAtCommand` |
| `expand:<tree-name>:<row>` | sets `IsExpanded` on the flattened tree's row |
| `check:<toggle-name>`, `changed-only` | toggles a named `ToggleButton` |
| `menu:<header>` | `RefreshMenus()` then raises `Clicked` on that `NativeMenuItem` |
| `click:<name or label>` | presses a `Button`; name wins over content, newest window first |
| `press:<x>,<y>[:<mods>]`, `move:...`, `release:...` | one pointer gesture, **driven in written order**; each part carries its own modifiers |
| `mouse-back:<x>,<y>` / `mouse-forward:<x>,<y>` | XButton1/2 press+release |
| `wheel:<x>,<y>:<delta>` | wheel over a point, positive up |
| `tooltip:<x>,<y>` | walks up from the hit element until something carries a tip, then opens it |
| `context:<x>,<y>` | raises `ContextRequestedEventArgs` (a synthesized right button does *not*) |
| `type:<text>` / `key:<gesture>` | text input / `KeyGesture.Parse` on the focused element |
| `select:<list-name>:<index>` | sets `SelectedIndex` + `ScrollRowIntoView` |
| `folder:<path>` \| `folder:cancel` | pre-answers the next clone-location question |
| `caret`, `stranded` | diagnostics; `stranded` reports virtualizing-panel children that are unrealized or arranged at their own width (the ghost-row bug) |

One shared `Avalonia.Input.Pointer TestPointer` (id 9001) - a press and its release must arrive on
the same pointer or nothing that tracks a press (drag distance, click count, capture) sees it.

Everything asynchronous a command does runs *before* the capture but does not *finish* before it: a
second, plain screenshot request is needed to see it.

## Theme, zoom, preferences, on-disk state

### ThemeManager

Singleton `ThemeManager.Current`. `UpdateTheme(name)` sets `Application.RequestedThemeVariant`,
**re-themes every registered highlighting definition first**, then raises `ThemeChanged`.

`ApplyHighlightingColors` writes colours onto the definition's named `HighlightingColor` instances
**in place**, because the `RichTextModel` holds references to those same instances. A snapshot of the
original (light, `.xshd`-default) colours is kept per definition so Light restores exactly. Dark uses
the hand-authored `SyntaxColorPalettes.CSharpDark` where one exists, else an algorithmic HSL
conversion: invert lightness with a 1.2 curve, then desaturate colours above 0.75 saturation so they
do not glow.

**Note:** nothing in the current UI calls `UpdateTheme` - there is no theme menu item. `Theme` is
`null` at startup, so the app runs Light. The machinery is complete and wired; only the command is
missing.

### Zoom

Two cooperating pieces: `MainViewModel.Zoom` -> the `LayoutTransformControl`'s `ScaleTransform`
(real relayout); and `ZoomState.PopupScale` -> a shared mutable `ScaleTransform` that `App.axaml`'s
`OverlayPopupHost` style points at. Popups are hosted by the window's overlay layer, *outside* the
scaled content, so they must be scaled where they are; a style has no data context, hence the static.

### ScopePalette

Two shared mutable brushes (`Accent`, `Tint`) referenced by `{x:Static}` from `App.axaml` and several
pane XAMLs. `Set(workspace)` picks blue `#3794FF` (whole change, tint opacity 0), purple `#A371F7`
(commit-by-commit, 0.10) or orange `#F0883E` (since last pass, 0.10).

### On-disk user data

`UserData` writes one small file per setting under `%LocalAppData%/stampeded` (on Linux
`~/.local/share/stampeded`). All reads and writes swallow `IOException` - a preference that cannot be
read is a preference that was never set.

| File | Owner | Content |
| --- | --- | --- |
| `zoom.txt` | `ZoomPreference` | the zoom, clamped on load |
| `window.txt` | `WindowPlacement` | `x y w h normal\|maximized` |
| `recent-repos.txt` | `RecentRepos` | up to 10 paths, existing directories only |
| `tab-rows.txt` | `TabRowsPreference` | `multi` / `single` |
| `diff-layout.txt` | `DiffLayoutPreference` | `side-by-side` / `unified` |
| `merge-method.txt` | `MergeMethodPreference` | gh flag name; default `merge` (not squash - the series a review was read as is worth keeping) |
| `delete-branch.txt` | `DeleteBranchPreference` | `true` / `false` |
| `build-solutions.txt` | `BuildSolutionPreference` | tab-separated `repoPath\tsolution` lines |
| `scope-mode.txt` | `ReviewScopes` | `commit` / `whole` |

Also written: `%LocalAppData%/stampeded/logs/test-*.log` (Tests pane), and the three
`~/.cache/stampeded/` directories owned by `Stampeded.Core`.

`WindowPlacement.Attach(window)` only records geometry while `WindowState == Normal`, and checks a
restored rectangle against the screens that *exist now* with an 80 px corner requirement, because a
window nobody can see cannot be dragged back.

### BusyTracker

Thread-safe reference-counted activity tracker; `Begin(label)` returns an `IDisposable`. Publishes
`Text` (labels joined with `·`), `IsBusy` and a braille `SpinnerFrame` on an 80 ms timer, always
marshalled to the UI thread. The Merge Queue pane deliberately reuses `SpinnerFrame` for its own row
spinners rather than running a second clock.

## Every pane

| Pane (id) | Shows | Data source | Commands |
| --- | --- | --- | --- |
| **Explorer** (`Explorer`) | scope header + **hosts `PrFilesPaneView` and `FileBrowserPaneView`** in a 2:3 split | `workspace.Scopes`, `ReviewChanged` | enter/step/exit commit scope, since-last-pass + baseline dropdown, VS Code, open PR, open Review doc, reload, close review |
| **PR files** (in Explorer) | tree of changed files, compacted single-child directory runs; per row: change marker, name, +/- counts, comment badge (amber open / green settled), `new!` since-last-pass, coverage badge, viewed checkbox | `workspace.ReadingOrder`, `Store.IsViewed`, `Comments`, `Coverage` | selection opens the file; Toggle Viewed; `TestsFirst` checkbox |
| **File browser** (in Explorer) | `SharpTreeView` over the head worktree, lazily enumerated, skipping `bin/obj/.git/.vs/node_modules` | `workspace.WorktreePath`, rebuilt on `SemanticsChanged` | double-click opens a source view; `RevealAsync` follows the active document |
| **Change map** (`Map`) | project -> file -> member, coloured by kind | `workspace.ChangeMap` | click jumps |
| **Structure** (`Structure`) | outline of the **active** diff, members tinted by how much of their range the change touches | `GetOutlineAsync(relPath, sideText)` - the *text on screen* | double-click jumps |
| **References** (`References`) | find-references hits, `*` marking hits on changed lines; also narrates semantic load state | `ReferencesAvailable`, `SemanticsChanged` | double-click opens |
| **Call graph** (`CallGraph`) | member -> `Incoming calls` / `Outgoing calls` buckets (lazily loaded, placeholder row holds the expander open) + per-call-site rows | `GetCallsAsync`, rooted by `CallGraphRequested` | "Only members this review changes" checkbox re-roots the tree, since children are fetched once per node |
| **Comments** (`Comments`) | drafts + posted comments, preview capped at 220 chars (long bodies made the layout pass crawl) | `Comments.Changed` | Add Draft, Refresh, Delete Draft, Mark Resolved/Unresolved/All, Open on host, Approve / Request Changes / Comment |
| **Checks** (`Checks`) | CI check runs sorted fail -> pending -> rest | `Host.GetChecksAsync`; publishes back via `workspace.SetChecks` | Refresh; double-click a failed run opens its failed-step log |
| **Merge queue** (`MergeQueue`) | the shared queue: position, PR, method, who queued it, a live note column; departed entries kept below with the reason read out of the ref's history | `MergeQueueService` | Add current PR, Remove, Move up/down, Empty, Clear errors, Clear lock, **Drive** (30 s poll - the only polling in the app) |
| **Tests** (`Tests`) | failure list + live output; spinner in the pane **and in the dock tab title** | `TestService` over the head worktree | Run/Cancel, Run+Coverage (-> `SetCoverage`), Run A/B (base then head, `TestRunComparison`, opens a side-by-side output document), Impacted filter, Clear |
| **Commits** (`Commits`) | commits of the review range │ files of the selected commit │ full message | `Scopes.GetCommitsAsync`, `Git.DiffNameStatusAsync` | double-click a file opens `OpenHistoricalDiffAsync` |
| **History** (`History`) | `git log --follow` of the active file (50), or a pickaxe search over selected text | `ActiveViewChanged`, `PickaxeRequested` | double-click opens that commit's diff |
| **Run** (`Run`) | project combo (executables first) + arguments + live output | `*.csproj` filtered by `Exe`/`WinExe` | Run/Stop (`dotnet run --project`), Clear |
| **Log** (`Log`) | the `CliLog` ring buffer, newest at the bottom, file:line references turned into links | `CliLog.Sink` - **setting it replays what was written before the pane existed** | Clear, Copy Selected, Copy All |
| **PR list** | open PRs sorted: review-requested-from-me, unreviewed, reviewed, approved-by-me, drafts. Not in the default layout - instantiated by `StartDocumentViewModel` | `Host.ListOpenPrsAsync` + viewer login | double-click opens a review; `base..head` range box |

`PassBaselineFlyout.ShowFor(sender)` builds the three-item `MenuFlyout` used by both the Explorer and
the Overview, marking the one in use with `•` and enabling only the available ones.

## Things that will bite a maintainer

1. **`TextEditor.ScrollToVerticalOffset` does nothing in AvaloniaEdit 12.** Always go through the
   editor's `ScrollViewer`.
2. **`TextView.InvalidateLayer` does not repaint layer children.** Loop over `textView.Layers` and
   `InvalidateVisual()` each.
3. **A subclassed `TextEditor` needs `StyleKeyOverride`** or it gets no template and no
   `ScrollViewer`.
4. **Captured pointer releases are raised on the capturing control.** Handle `PointerReleased` on
   `TextArea`, not `TextView`.
5. **A `TextBox` with `AcceptsReturn` handles Enter before your handler.** Use
   `handledEventsToo: true`.
6. **Never add an `InputGesture`/`Gesture` for a single letter** - on macOS it becomes a real key
   equivalent that beats the focused text box.
7. **A `NativeMenuItem` has no name, no `ItemsSource` and no tooltip flag of its own.**
8. **A disabled control answers no hit test in Avalonia**, so a tooltip on one is unreadable - wrap
   it in a transparent `Border` carrying the tip.
9. **`ItemsControl.ScrollIntoView` strands containers.** Use `ListScrolling.ScrollRowIntoView`; the
   `stranded` screenshot command detects the failure.
10. **Clear-then-await-then-add doubles a bound list.** Use `Collections.Replace` - the events these
    lists refill from fire more than once per review.
11. **Document line numbers are not stable.** Comment-thread splices renumber everything below; carry
    caret, folds and gaps as *blob* positions.
12. **The since-last-pass scope's base is a tree, not a commit.** Anything wanting history or a
    checkout must check `Scopes.InSinceLastPass` first.
13. **Adding a document or pane type means three edits**: the class, an entry in `ViewLocator`, and -
    for a pane - registration in `StampededDockFactory.CreateLayout` plus a `View > Panes` menu item.
14. **Adding a document command means four edits**: the `ReviewCommands` flag, both
    `IReviewDocumentView` implementations, `MainWindow.RefreshMenus`, and the menu item.

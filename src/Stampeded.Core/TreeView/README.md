SharpTreeView
=============

Vendored from ILSpy (https://github.com/icsharpcode/ILSpy), MIT license — see this
repository's LICENSE. Namespaces were rewritten from `ICSharpCode.ILSpyX.TreeView` /
`ICSharpCode.ILSpy.Controls.TreeView` to the `Stampeded` equivalents; the files otherwise
keep their original copyright headers and are meant to stay close to upstream so fixes can
be taken across.

| Copied from | To |
|---|---|
| `ICSharpCode.ILSpyX/TreeView/*.cs` | `Stampeded.Core/TreeView/` |
| `ICSharpCode.ILSpyX/TreeView/PlatformAbstractions/*.cs` | `Stampeded.Core/TreeView/PlatformAbstractions/` |
| `ILSpy/Controls/TreeView/*` | `Stampeded/Controls/TreeView/` |
| `ILSpy/Controls/TreeLines.cs` | `Stampeded/Controls/` |

## Why this and not Avalonia's TreeView

`TreeFlattener` projects the hierarchy into a single `IList`, so the control is a
virtualized flat list and a node's depth costs an indent value rather than a nested
container per level. A call hierarchy can recurse as far as the reader keeps expanding,
which nested containers do not survive.

`SharpTreeNode` also carries `LazyLoading` / `LoadChildren`, so a level is fetched on first
expansion rather than up front.

## Deliberate omissions

- `IRichTextNode` / `RichNodeText` were dropped: they render coloured inlines for ILSpy's
  analyzer nodes and reach into its composition, settings and theme services. The cell
  template binds the node's plain `Text` instead.
- The two focus brushes the template needs are defined in `App.axaml` as
  `Stampeded.TreeFocusFill` / `Stampeded.TreeFocusBorder`.

## Deliberate divergences

- The cell template binds `Foreground` to the node's own property. `SharpTreeNode` has
  always exposed it; upstream's template does not bind it because ILSpy colours rows
  through `RichNodeText`, which is not ported. The change-tinted trees (Structure, Map)
  need per-node colour. A node that sets none binds null, which would blank the label, so
  the binding falls back to the theme foreground.
- `SharpTreeView.OnSelectionChanged` guards against null item lists. Upstream dereferences
  them directly, which is safe while a tree's root outlives its pane; here the root is
  replaced whenever the active document changes, and a selection change raised during that
  teardown carries no lists. Worth reporting upstream - the bug is latent there too.

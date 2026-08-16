Icons used in Stampeded
-----------------------

Copied from ILSpy (https://github.com/icsharpcode/ILSpy, MIT license, see this
repository's LICENSE), which documents their origin as follows:

|              | SVG |  Origin                                                        |
|--------------|-----|----------------------------------------------------------------|
| Assembly     |  x  |  VS 2017 Icon Pack (Reference)                                 |
| Class        |  x  |  VS 2017 Icon Pack (Class)                                     |
| Constructor  |  x  |  based on VS 2017 Icon Pack (Method) using a different colour  |
| Delegate     |  x  |  VS 2017 Icon Pack (Delegate)                                  |
| Enum         |  x  |  VS 2017 Icon Pack (Enumerator)                                |
| Event        |  x  |  VS 2017 Icon Pack (Event)                                     |
| Field        |  x  |  VS 2017 Icon Pack (Field)                                     |
| Folder.Closed|  x  |  VS 2017 Icon Pack (Folder)                                    |
| Folder.Open  |  x  |  VS 2017 Icon Pack (FolderOpen)                                |
| Indexer      |  x  |  VS 2017 Icon Pack (Indexer)                                   |
| Interface    |  x  |  VS 2017 Icon Pack (Interface)                                 |
| Method       |  x  |  VS 2017 Icon Pack (Method)                                    |
| Operator     |  x  |  VS 2017 Icon Pack (Operator)                                  |
| Property     |  x  |  VS 2017 Icon Pack (Property)                                  |
| Resource     |  x  |  VS 2017 Icon Pack (Document)                                  |
| Struct       |  x  |  VS 2017 Icon Pack (Structure)                                 |
| ViewCode     |  x  |  VS 2017 Icon Pack (GoToSourceCode)                            |
| SubTypes     |  x  |  based on VS 2017 Icon Pack (BaseType) rotated +90 degrees      |
| SuperTypes   |  x  |  based on VS 2017 Icon Pack (BaseType) rotated -90 degrees      |

Toolbar icons
-------------

The commands in the toolbars - the header over the changed-file list, the overview, the tests
and run panes, the start page rows - use the Visual Studio 2026 Image Library directly, taken
from it unmodified and under the name it gives them:

| Command                          | Image                |
|----------------------------------|----------------------|
| Commit by commit                 | Commit               |
| Since last pass                  | History              |
| Whole change                     | Diff                 |
| Previous / next commit           | Previous / Next      |
| Open in VS Code                  | VisualStudioCode     |
| Open on GitHub                   | GitHub               |
| Approve / Decline                | Checkmark            |
| Bounce                           | Undo                 |
| Reload, refresh                  | Refresh              |
| Close review                     | Close                |
| Run tests / cancel               | RunTest / Cancel     |
| Run + coverage                   | CodeCoverage         |
| Run A/B                          | CompareFiles         |
| Impacted filter                  | Filter               |
| Clear output                     | ClearWindowContent   |
| Run application                  | Run                  |
| Fetch, pull, push, rebase        | Fetch, Pull, Push, Rebase |
| Branch from stash                | Branch               |
| Delete branch                    | Delete               |
| Open worktree                    | FolderOpened         |
| Open repository / from URL       | OpenFolder / OpenWebSite |
| Add draft comment                | Comment              |
| Merge                            | Merge                |

### File kinds in the explorer tree

Taken from the same VS 2026 library, under their own names, and mapped to extensions in
`Images.ForFileName`. A file whose kind is not among these keeps the plain document icon:
guessing at an unknown extension would say something the name does not.

| Shown for                              | Image (VS 2026)  |
|----------------------------------------|------------------|
| .cs / .vb / .fs                        | CSFileNode / VBFileNode / FSFileNode |
| .csproj and friends / .sln, .slnx      | Project / Solution |
| .xml, .config, .props, .targets, .resx | XmlFile          |
| .xaml, .axaml                          | WPFFile          |
| .json                                  | JSONScript       |
| .md                                    | MarkdownFile     |
| .txt, .log, .csv                       | TextFile         |
| .yml, .yaml                            | YamlFile         |
| .html, .cshtml, .razor                 | HTMLFile         |
| .js, .ts and the rest of that family   | JSScript         |
| .css, .scss, .less                     | StyleSheet       |
| .ps1 / .sh, .bat, .cmd                 | PowershellFile / Console |
| images (.png, .svg, .ico, ...)         | Image            |
| .pfx, .cer, .snk                       | Certificate      |
| .db, .sqlite                           | Database         |
| *.lock                                 | Lock             |
| .gitignore, .gitattributes, .gitmodules| Git              |
| .bin, .dat, .pdb, .zip, .nupkg         | BinaryFile       |
| .editorconfig                          | Settings         |

The library is licensed, not public domain: it may be used in applications developed with the
Visual Studio family of products, and its EULA - shipped with the download - is what governs
that. The images are used here as icons for commands, not as content of their own.

## Information on Sources

* VS 2017 Image Library https://www.microsoft.com/en-us/download/details.aspx?id=35825
* VS 2026 Image Library https://www.microsoft.com/en-us/download/details.aspx?id=35825

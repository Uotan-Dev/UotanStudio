# Comparison services

`IComparisonService` is the document's file-operation boundary. `ComparisonService`
returns plain directory items, text lines, differences and `ComparisonMetadata`;
it does not reference ViewModels, controls, localization or document state.
Its constructor receives algorithm instances, format detection, the format catalog
and a warning callback. `CreateDefault` binds the desktop registry and log sink.
The existing public algorithm `Instance` properties remain compatible; the default
comparison backend owns separate stateless algorithm instances.

`CompareViewModel` owns paths, selection, cancellation, metadata caches and lazy hex
rows. Its parameterless constructor preserves existing document creation; the internal
constructor accepts an explicit backend. `ComparisonRowBuilder` belongs to the
presentation layer and translates data into aligned display rows without file I/O.
Folder commands and filtering are in `CompareViewModel.Folders.cs`; that partial
file shares the document's existing state and is not a separate business module.

The document is laid out as a list of `CompareColumnViewModel` file columns, so the
same page works as a plain file browser: a column browses a directory tree (lazily
loaded on the first expansion, collapsed right after import) or a single file's
content, and columns can be added without an upper limit. `CompareTreeNodeViewModel`
reuses `ExplorerIconCatalog` for its glyphs, which is also where `ExplorerNode`
resolves its icons. Starting a comparison (`ToggleCompareCommand`) requires exactly
two columns with content, assigns them the `Left`/`Right` side and shares one row
list with both; ending the comparison releases the result and returns both columns
to browsing. Only the two compared columns share scrolling and selection.

A folder comparison keeps the browsing look instead of flattening the tree: both
columns show the same collapsed, lazily expanded trees with the explorer glyphs. The
comparison itself is one aligned row list shared by both columns
(`ComparisonRowBuilder.ForTree` flattens `CompareTreeNodeViewModel` by expansion
state): same-name entries share a row, single-side entries stay blank on the other
side, "different" rows are coloured as a whole, single-side entries tint their own
side, and both sides share one node — so expanding either side expands both, and the
shared list is what keeps the panes aligned while they scroll together. Directory
statuses are aggregated bottom-up, so the initial folder is coloured when anything
below differs. Double-clicking a file sends `OpenFileCompareInNewTabRequestMessage`,
which the host turns into an additional compare document (the current folder
comparison is left untouched), and the missing side stays empty when the file has no
counterpart. `CompareStatusBrushes`, `ComparisonRowBuilder.ForTextSingleSide` and the
left/right tint brushes are the row-level pieces a single-side file comparison needs;
browsing a folder uses the same tree rows without any status colour.

Text comparisons are editable: `CompareRowViewModel` tracks whether either side
"has" a row (`HasLeftLine` / `HasRightLine`, which is how an empty line differs from a
missing one), exposes the line text as an observable property and recomputes the
whole-row diff state on every change. The toolbar's copy buttons (shown only while
comparing, centred in the toolbar) push the selected rows to the same rows on the
other side — a missing target row appears, a missing source row deletes the target's
row — and the column header's save button writes that side's lines back through
`IComparisonService.SaveTextAsync`, which keeps the file's BOM-derived encoding, its
line ending and its trailing-newline state while swapping a temporary file in
atomically. Folder entries, text rows and the file explorer share the row context
menu (`CompareRowMenuKind`); file operations reuse `ExplorerFileOperations` and the
explorer dialogs, dropping paths onto a folder row copies into that folder, and the
column splitters are disabled while comparing so the panes keep equal widths.

Line editing is structural as well: the text rows live in an `ObservableCollection`, so
`SplitTextRow`, `JoinTextRowWithNext` / `JoinTextRowWithPrevious` and
`DeleteSelectedTextRows` insert, merge and delete rows while the caret is restored to
the edited row — a split leaves the new row on the edited side only, a merge appends
the neighbouring line and drops a row that ends up empty on both sides, and every
change recomputes that row's whole-row diff state. Rows can also be dragged out: the
pointer press/move pair starts `DragDrop.DoDragDropAsync` with the row's full path as
the payload (text format, validated as an existing path on drop), so dropping a row on
another folder row copies it there.

## Behavior boundaries

- A known firmware extension takes priority over a successful text probe.
- Single-side text reading preserves BOM detection, line order and empty lines,
  with limits of 2,000,000 lines and 256 MiB of file bytes. These are input limits,
  not a promise about total managed memory consumption.
- Single-side directories retain ordinal relative-path sorting. Two-side directory
  comparison retains `FolderCompareService`'s existing ordering and traversal.
- Metadata reads await handlers asynchronously and never unpack files. Unknown
  formats and handler-declared failures yield the unrecognized placeholder; declared
  failures are logged. Exceptions and cancellation propagate to the document.
- The backend does not retain paths or a current cancellation source. Each call uses
  explicit inputs. The document still checks cancellation before publishing results.
- Single-side row projection remains on background tasks. Display state and
  notifications remain on the document's existing continuation context.
- Binary scanning remains an explicit request when opening the hex view. Existing
  bitmap allocation and lazy hex-row ownership are unchanged.
- Copy and delete continue to use the original safe relative-path resolver. Their
  document commands retain the original cancellation policy; changing a read request
  does not cancel an already requested file mutation.

`ComparisonServiceTests` exercises the backend without a UI platform, including
format priority, input limits, BOM handling, resource release, asynchronous metadata,
failure policy, concurrent requests and cancellation. Existing text, binary and
folder comparison tests continue to cover the underlying algorithms. These tests
do not validate display bindings, navigation or rendered layout.

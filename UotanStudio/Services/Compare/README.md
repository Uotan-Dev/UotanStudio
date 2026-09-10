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

# Image browsing

`ImageBrowserSession` owns an opened filesystem parser or `ArchiveBrowser`, its flat
entry index, and any additional raw or sparse source stream. It returns
`ImageBrowserEntry` data and extracts selected relative paths. It has no ViewModel,
Avalonia, document navigation, message-bus or localization-service dependency.
The caller may supply the existing localized unrecognized-image message to `Open`.

The implementation is grouped by responsibility:

- `ImageBrowserSession.cs`: ownership, extraction serialization and disposal.
- `ImageBrowserSession.Loading.cs`: detection, raw/sparse source opening and indexing.
- `ImageBrowserSession.Extraction.cs`: selected-subtree extraction. EXT4 and F2FS
  share entry writing; EROFS and archives keep their existing format-specific policies.

`FileManagerEditorViewModel` owns a single session and the UI state. It still builds
the display tree on the worker thread via `FileManagerTreeBuilder`; navigation,
selection, counters and notifications remain in the presentation layer. Opening as
text or hex is decided by the existing workspace/open-mode layer, not by this service.

## Resource lifecycle

Raw streams are opened before parser construction. A parser-construction or indexing
failure releases the parser when available and always releases the owned source.
Sparse logical streams retain the sparse file and its underlying physical handle.
Archive loading keeps the existing temporary-file ownership and expansion limits in
`ArchiveBrowser`.

An accepted extraction holds a session lease. Closing the session rejects new leases
without waiting on the UI thread; the last accepted operation releases the resources
when it finishes. Parser access is serialized because filesystem parsers share stream
positions. This preserves the existing non-cancellable extraction behavior while
preventing disposal during an active read. A queued operation which has not acquired
a lease can be rejected after closing.

The document releases a newly loaded session if tree construction or publication is
cancelled or fails. A superseded load cannot clear the newer load's busy state or
publish its error. A directory-picker continuation checks that it still refers to
the same session before starting extraction.

## Verification boundary

`ImageBrowserSessionTests` exercises raw and sparse EXT4/EROFS/F2FS browsing,
selected-subtree extraction, framework-generated TAR and GZip/TAR input, source-handle
release on successful and failed loads, cancellation before opening, and deferred
disposal. Existing `ArchiveBrowserTests` cover archive bounds and explicit open modes.
These are backend tests; they do not initialize a UI platform or validate rendered
trees, selection bindings or interactive navigation. This extraction reuses existing
format readers and write policies; it is not a new filesystem conformance audit.

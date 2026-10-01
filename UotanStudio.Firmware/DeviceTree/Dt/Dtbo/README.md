# DTBO Repacking

`DtFormatHandler` supports DTBO files and directories containing `metadata.json`.
The CLI recognizes these directories automatically:

```text
firmware unpack dtbo.img -o dtbo_out
firmware pack dtbo_out -o dtbo-new.img
firmware pack dtbo.img -o dtbo-copy.img
```

The explicit format name remains `Device Tree (DTB/DTBO)`. A single DTB input
still produces a single DTB; the output filename does not select a container.
The `Firmware.Unpack` and `Firmware.Pack` workflow nodes use the same backend.

## Extracted Files

- `overlay_00.dtb`, `overlay_01.dtb`, ...: decompressed overlays in table order.
- `metadata.json`: unified metadata; `configuration` contains format
  version, image capacity, header and ordered entry metadata.
- `original.dtbo.img`: original bytes, including gaps, padding and trailing data.

Edit or replace overlays, or edit the ordered `configuration.entries` array to change selection
metadata, add entries, remove entries or reorder them. Files must be local
filenames, without directory traversal or symbolic links. The template requires
additional disk space equal to the original image size.

The following field paths are relative to `configuration`.
`header.version` accepts 0 and 1. `header.pageSize` is preserved as metadata;
blobs are contiguous when rebuilding and are not page-aligned. Entry `dtSize`
and `dtOffset`, header `totalSize` and `entryCount` are recalculated after edits.
Extended header bytes and the gap before the entry table come from the template.
Changing `headerSize` or `entriesOffset` of that template is unsupported.

The entry model stores the four words after `id` and `rev` as `custom0..3`:

| Version | `custom0` | `custom1..3` |
| --- | --- | --- |
| 0 | First custom word | Remaining three custom words |
| 1 | Flags; low nibble: 0 = raw, 1 = zlib, 2 = gzip | Upstream v1 custom words 0..2 |

All bits are preserved. For v1, change only the low nibble of `custom0` to select
compression. Numeric JSON values are decimal, not hexadecimal literals.

`imageSize` is the output file capacity. Extracted configurations default to the
original file size. Rebuilding fails if the new table and blobs do not fit.
For an unsigned image, explicitly increase it or set it to `0` to produce the
minimum-sized container. Zero padding is recreated when a larger capacity is set.

## Preservation and Authentication

When header fields, entries, decoded contents and capacity are unchanged,
packing copies the validated template byte-for-byte. This also preserves shared
blob offsets, compression bitstreams, internal gaps and any AVB footer.
This is preservation of existing authentication data, not signature verification.

After edits, the backend rebuilds the table and recompresses overlays. Arbitrary
inter-blob gaps are normalized. If bytes after the original `total_size` contain
any nonzero data, packing fails instead of attaching a stale AVB footer or opaque
vendor trailer. Strip/recreate authentication through an AVB-aware workflow with
the appropriate signing policy; this DTBO backend does not sign images.

## Creating a New Image

A directory can be created without a template. Minimal v0 unified metadata:

```json
{
  "schema": "UotanStudio.Firmware.Repack",
  "version": 1,
  "formatName": "Device Tree (DTB/DTBO)",
  "canPackDirectory": true,
  "configuration": {
  "formatVersion": 1,
  "hasOriginalImage": false,
  "imageSize": 0,
  "entries": [
    {
      "file": "board.dtb",
      "metadata": {
        "id": 123,
        "rev": 1,
        "custom0": 0,
        "custom1": 0,
        "custom2": 0,
        "custom3": 0
      }
    }
  ]
  }
}
```

For v1, supply a complete `header` object with `magic: 3619138334`,
`headerSize: 32`, `entrySize: 32`, `entriesOffset: 32`, `pageSize: 2048`,
and `version: 1`. Set each entry's compression flags as described above.

## Library APIs and Limits

- `ReadTable(Stream)`: reads bounded metadata only; leaves the stream open.
- `Unpack(path, directory, options, cancellationToken)`: stages all files before
  publishing them; respects overwrite and per-overlay output limits.
- `PackDirectory(directory, path, cancellationToken)`: stages encoded blobs on
  disk, calculates layout, then atomically replaces the output file.
- `Repack(inputPath, outputPath, cancellationToken)`: validates decompression and
  copies an unchanged image atomically; supports the same input/output path.
- `Pack(dtbs, entries, pageSize, version)`: small in-memory convenience API with
  metadata and compression support; combined input limit is 256 MiB.

Tables are limited to 65,536 entries and manifests to 32 MiB. File-based paths
stream image data; no whole-image allocation is used. `Read(byte[])` defaults to
a 256 MiB combined decoded output limit; direct `Repack` uses 256 MiB per overlay. gzip CRC/ISIZE and
zlib Adler-32 trailers are checked explicitly, including premature EOF cases.

Modified images still require device-specific overlay and boot validation.

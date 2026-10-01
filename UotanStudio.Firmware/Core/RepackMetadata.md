# Extraction Directory Metadata

Successful format-handler extraction creates `metadata.json` in the output root.
Application firmware extraction services also write this file when they call
parsers directly. Ordinary filesystem subdirectories do not represent independent
images and do not receive separate metadata. Metadata is written only during
extraction: an existing extraction directory without it gains one only by being
extracted again from its source image.

When a project is open, the application services pass a metadata root and the
metadata file plus the filesystem sidecars are stored under
`{project root}/.uotan/metadata/<partition name>/`; the extraction directory
itself contains no metadata files. Without a project (for example the CLI), the
metadata file is written directly into the extraction directory. Reads use
exactly one location: when `RepackMetadata.DefaultMetadataRoot` is set (project
open) the consolidated location keyed by the extraction directory name or its
detected partition name is used exclusively; otherwise the extraction directory
itself is checked. There is no cross-fallback between the two layouts.
`ProjectContextService` sets and clears the ambient default root on project
open/close.

One file carries everything. `fileSystemPreservation` merges the structured
override tables (`partition`, `fsConfig`, `fileContexts`, `partitionInfo`,
`rebuild`) with the verified capture record (`format`, `sourceLength`,
`sourceHash`, `configurationHash`, `configurationJson`, `projection`,
`imagePaths`, `parsedEntries`). No sibling manifest, override document or
`.filesystem/` directory is produced, so the deepest path is
`{project root}/.uotan/metadata/<partition name>/metadata.json`. Editing the
payload changes the document digest, so a hand-edited file is rejected instead of
silently steering a rebuild.

Example for an EROFS image:

```json
{
  "schema": "UotanStudio.Firmware.Repack",
  "version": 1,
  "formatName": "EROFS",
  "sourceFileName": "vendor.img",
  "sourceLength": 1048576,
  "sourceProperties": {
    "BlockSize": "4096",
    "VolumeName": "vendor"
  },
  "packingOptions": {
    "BlockSize": "4096",
    "VolumeName": "vendor"
  },
  "configuration": {
    "fileSystem": "EROFS",
    "superblock": { "volumeName": "vendor", "blockSizeBits": 12 }
  },
  "canPackDirectory": true,
  "packingNotes": "Use packingOptions and configuration. Source properties are descriptive; other fields are not automatically restored."
}
```

`sourceProperties` contains the properties exposed by the source reader and any
additional properties reported during extraction. These are observations, not
an assurance that every property can be restored. For example, Sparse filesystem
extraction records `InnerFormat` and `FilesystemBlockSize` as well as outer Sparse
metadata. Source paths are reduced to filenames so directories remain portable.

`packingOptions` contains options currently consumed by directory packers:

EXT4, EROFS and F2FS also record the parsed original superblock in
`configuration.superblock`, with numeric fields and a hexadecimal UUID.
EXT4 includes block/inode geometry, free counts, feature flags, volume name,
journal and checksum fields. EROFS includes build time, compression algorithm
flags, metadata/xattr locations and device/fragment/metabox fields. F2FS includes
sector/block/segment geometry, checkpoint/NAT/SIT/SSA/main-area locations and
feature flags. These are the fields supported by the existing superblock readers,
not a complete backup of all on-disk metadata. Sparse-wrapped filesystems record
the same inner superblock through a bounded logical stream; the outer format
identity and its packing limitations remain unchanged. Superblock observations
are not applied as packing options; physical layout and allocation are rebuilt.

EROFS also records `configuration.compression`, including algorithm-specific
dictionary/window and LZ4 distance/cluster limits. Edited EROFS builds derive
these settings and per-file inode attributes from the hash-verified captured
metadata. They preserve UUID, raw volume bytes and source timestamps. The on-disk
format does not identify encoder level or distinguish LZ4 from LZ4HC, so
`compressionLevel` is explicitly null. Compressed data boundaries, physical
layout and final image size can change. See [EROFS rebuild behaviour](../../docs/filesystem-erofs.md)
for native-tool and kernel validation and the tested feature boundaries.

| Format | Options |
| --- | --- |
| EXT4 | `BlockSize`, `FsConfigPath`, `FileContextsPath` |
| EROFS | `BlockSize`, `VolumeName`, `FsConfigPath`, `FileContextsPath` |
| F2FS | `BlockSize`, `SegmentSize`, `VolumeName`, `FsConfigPath` |
| CPIO | (consumes `<partition>_fs_config` for uid/gid/mode) |

Options use invariant-culture numeric strings. `configuration` embeds the original
format configuration: DTBO uses `header`, ordered `entries`, `imageSize` and
`hasOriginalImage`; Boot uses `magic` and `header`; vendor_boot uses `magic`,
`vendorHeader` and `ramdiskTable` (including names, types, offsets and board IDs).
There are no separate `header`, `header.json` or `dtbo.json` configuration outputs.
Boot headers and DTBO tables use camelCase property names and numeric enum values.
Sizes, offsets and hashes derived from components are recalculated by the packers.
Boot packing reads the raw `ramdisk` / `vendor_ramdisk` components; decompressed
CPIO copies and split vendor ramdisks are inspection outputs, not alternate inputs.
CPIO currently rebuilds as newc;
Payload builds unsigned full images; LP builds new default groups and geometry.
These limitations are recorded in `packingNotes`. File ownership, extended
attributes and every vendor-specific header field are not automatically restored
by this directory-level metadata.

Without an explicit `--format`, `firmware pack <directory>` reads the metadata to
select the registered format. Unsupported directory packing, invalid metadata
and unknown formats fail instead of falling back to EXT4. An explicit format
overrides the metadata identity. The firmware workflow pack node, image panel and MCP automatic
packing use the same format identity and registered backend. In the image panel,
directories with metadata use that configuration; directories without metadata
continue to use the project packing settings.

Double-clicking an extraction directory in the project explorer opens its
`metadata.json` in the text editor. The path resolution uses the
same single-location rule: the consolidated metadata file under
`.uotan/metadata/<partition name>/` when a project is open, the local file
otherwise. The file is opened even if it contains an unfinished JSON edit so it
can be repaired. Directories without any discoverable metadata keep the default
navigation behavior. The metadata file is shown in the project
explorer like any other file.

The generic metadata file is excluded from filesystem, CPIO, NTPI, NB0, OPS and
Sparse directory inputs. Image-only packers select `*.img` inputs. Only
files bearing this schema are excluded: an unrelated payload file with the same
name is preserved, and extraction reports the reserved-name collision instead
of replacing it with metadata.

Metadata uses source-generated JSON serialization, a 64 MiB read/write limit and
atomic replacement. It is published only after successful extraction. The format
handler's `--no-overwrite` policy also protects existing directory metadata.

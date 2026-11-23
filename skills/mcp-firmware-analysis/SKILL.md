# MCP Firmware Analysis SOP

## When to use
- User wants firmware format detection, metadata, partition/GPT, or container inspection.

## Inputs
- `path`: sandbox-relative firmware/image path.

## Steps (via `execute_action`)
1. `detect_format` `{ "filePath": path }`
2. If recognized container/image: `read_metadata` `{ "filePath": path }`
3. If partitioned image: `read_gpt` `{ "filePath": path }`
4. If archive/container entries needed: `extract_container_entries` only when user asks to extract; otherwise report metadata.
5. For large outputs, read `ArtifactRef` via `artifact_read`.

## Output
- Summary: detected format, key metadata, partitions/entries count, warnings.
- Never claim destructive actions performed.

## Safety
- Read-only by default. No device writes. No paths outside sandbox.

# MCP Modify & Repack SOP

## When to use
- User wants to unpack firmware, modify allowed contents, repack, and verify.

## Inputs
- `sourcePath`, `workDir`, `outputPath`, modification request.

## Steps
1. `detect_format` and `read_metadata` for source.
2. `unpack_firmware` into sandbox workDir.
3. Apply requested modifications with appropriate internal actions (not direct destructive flash actions).
4. `pack_firmware` to outputPath.
5. Verify with `detect_format`/`read_metadata` and report checksums/sizes as returned by tools.

## Output
- Artifact refs for large logs; summary of changed files and output path.

## Safety
- All paths sandbox-confined. No flash execution in this SOP.

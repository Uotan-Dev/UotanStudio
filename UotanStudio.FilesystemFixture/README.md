# UotanStudio.FilesystemFixture

手动的文件系统往返验证宿主。它解包真实镜像、保留 preservation metadata，再重打包并对比清单。供 `tools/gki-fs-lab` 调用，不进入 `UotanStudio.slnx`。

```text
UotanStudio.FilesystemFixture roundtrip \
  --format EROFS \
  --source image.erofs \
  --work work/erofs \
  --repacked rebuilt.erofs \
  --manifest erofs.tsv \
  --report managed-erofs.json
```

- `--format`：`EXT4`、`EROFS` 或 `F2FS`。
- `--source`：主线工具生成的原始镜像。
- `--work`：解包与缓存目录；命令会清空同名的 `extracted-<format>`。
- `--repacked`：重打包镜像输出路径。
- `--manifest`：给客户机使用的 TSV 清单。
- `--report`：托管往返 JSON 报告。
- `--allow-fresh true`：允许 preservation 不可用时退化为全新目录构建；默认拒绝。

退出码 0 表示 preservation 成功且清单一致；非 0 表示解包、重打包或清单比较失败。

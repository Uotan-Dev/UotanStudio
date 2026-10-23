# UotanStudio.FilesystemFixture 项目指令

在本目录内修改手动文件系统夹具前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目是手动验证宿主，已在 `UotanStudio.slnx` 内，随解决方案构建、format 与测试门一起运行；真实镜像往返仍需单独执行。
- 只引用 `UotanStudio.Firmware` 与 `UotanStudio.Hash`。不要在这里改生产格式行为，也不要把夹具专用开关加到生产项目。
- csproj 关闭 AOT/裁剪分析，因为这是带 `System.Text.Json` 反射序列化的手动工具；不要把这组设置复制到生产项目。

## 行为

- 输入镜像按不可信输入处理；解包、重打包和读取都走 `Firmware` 的公开 API。
- 默认要求 `FileSystemPreservation.TryRestore` 成功；只有显式 `--allow-fresh` 才允许全新构建。
- 清单必须覆盖路径、类型、mode、uid/gid、大小、内容 SHA-256、符号链接、硬链接和 xattr。
- 临时目录由调用方清理；夹具不把镜像或提取树写进仓库。

## 运行

```text
dotnet build UotanStudio.FilesystemFixture/UotanStudio.FilesystemFixture.csproj -c Debug
dotnet run --project UotanStudio.FilesystemFixture/UotanStudio.FilesystemFixture.csproj -c Debug -- roundtrip --format EROFS --source <image> --work <dir> --repacked <image> --manifest <tsv> --report <json>
```

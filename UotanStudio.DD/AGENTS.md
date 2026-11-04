# UotanStudio.DD

跨平台磁盘镜像烧录支持库。把 raw/压缩/ZIP 镜像写入 Windows 物理磁盘、Linux 块设备或 macOS 裸磁盘，并提供驱动扫描、驱动兼容性约束、回读校验、进度与取消。

## 边界

- 只依赖压缩与摘要支持库，不依赖设备通信、资源存储、固件处理或界面层。
- 压缩解码只经 `CompressionService`，ZIP 只经 `ZipContainer`，摘要只经 `Sha256.CreateAccumulator()`。
- 本库负责操作系统块设备（Windows `\\.\PhysicalDriveN`、Linux `/dev/*`、macOS `/dev/rdisk*`）；USB、串口、adb 与 HDC 设备协议不在本库内。
- 不提供 UI；调用方通过 `FlashProgress` 展示阶段、百分比、速度与剩余时间。

## 目录

| 目录 | 职责 |
| --- | --- |
| `Images/` | 扩展名解析、raw/gzip/xz/bzip2/ZIP 探测与流式解码 |
| `Drives/` | `FlashDrive` 模型、驱动兼容性策略、平台扫描器 |
| `Writing/` | `IDiskTarget` 契约、文件目标与平台裸设备写入器 |
| 根目录 | `ImageFlasher` 编排、进度/结果/异常与安全开关 |

## 安全不变量

- `FlashOptions.ConfirmOverwrite` 必须显式为 `true`，否则在任何 I/O 之前抛出 `FlashException`。
- 系统盘、包含源镜像的盘、写保护盘、容量不足的盘一律拒绝；大于 128 GB 的盘只有在 `AllowLargeDrive` 为 `true` 时才放行。
- 写入前必须卸载/锁定目标卷；卸载失败即失败，不降级为带挂载写入。
- 每个块只读取一次并扇出到全部目标；默认写入后逐字节回读校验。
- 平台扫描/写入函数不可在单元测试中触碰真实磁盘；测试只使用 `FileDiskTarget` 或内存目标。

## 平台覆盖

| 平台 | 扫描 | 写入 | 已执行验证 |
| --- | --- | --- | --- |
| Windows | `FindFirstVolume` + `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`、`IOCTL_DISK_GET_LENGTH_INFO`、`IOCTL_STORAGE_QUERY_PROPERTY` | `FSCTL_LOCK_VOLUME`、`FSCTL_DISMOUNT_VOLUME`、原始句柄 `FileStream` | 构建与文件目标测试；未在真实磁盘执行 |
| Linux | `/sys/block` + `/proc/mounts` + `/proc/self/mountinfo` | `umount2`（必要时 `MNT_DETACH`）后打开 `/dev/*` | 仅构建；未在 Linux 主机执行 |
| macOS | `diskutil list -plist` + `diskutil info -plist` | `diskutil unmountDisk` 后打开 `/dev/rdisk*` | 仅构建；未在 macOS 主机执行 |

真实磁盘写入需要管理员/root 权限；未执行的原生挂载与真实介质验证不得写成已通过。

## 已知限制

- Windows 只自动锁定 `<盘符>:\` 形式的挂载点；目录挂载点会直接拒绝并要求手动卸载。
- Linux 在无法把 `/` 映射回物理盘时（例如 LVM/overlay 根）把所有非可移动盘标记为系统盘，优先保证不误擦启动盘。
- 压缩流的解压后大小未知，容量检查按磁盘上的文件大小估算；写阶段的百分比可能为空，校验阶段仍提供百分比与剩余时间。
- ZIP 镜像必须恰好包含一个文件条目，多条目归档直接拒绝。
- 不支持 `.dmg` 与 `.etch` 容器。

## 验证命令

```powershell
dotnet build "UotanStudio.DD\UotanStudio.DD.csproj"
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.DD --minimum-expected-tests 1
```

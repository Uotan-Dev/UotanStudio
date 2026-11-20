# UotanStudio.DD

跨平台磁盘镜像烧录支持库。把 raw/压缩/ZIP 镜像写入 Windows 物理磁盘、Linux 块设备或 macOS 裸磁盘，并提供驱动扫描、驱动兼容性约束、回读校验、进度与取消。

## 边界

- 只依赖压缩与摘要支持库，不依赖设备通信、资源存储、固件处理或界面层。
- 压缩解码只经 `CompressionService`，ZIP 只经 `ZipContainer`，摘要只经 `Sha256.CreateAccumulator()`。
- 本库负责操作系统块设备（Windows `\\.\PhysicalDriveN`、Linux `/dev/*`、macOS `/dev/rdisk*`）与光盘刻录机（Windows IMAPI2、Linux `sr` + `xorriso`、macOS `drutil`）；USB、串口、adb 与 HDC 设备协议不在本库内。
- 不提供 UI；调用方通过 `FlashProgress` 展示阶段、百分比、速度与剩余时间。

## 目录

| 目录 | 职责 |
| --- | --- |
| `Images/` | 扩展名解析、raw/gzip/xz/bzip2/ZIP 探测与流式解码 |
| `Drives/` | `FlashDrive` 模型、驱动兼容性策略、平台扫描器 |
| `Writing/` | `IDiskTarget`、`IDiscWriter`/`OpticalDiscSession` 契约、文件目标与平台裸设备/光盘写入器 |
| 根目录 | `ImageFlasher` 编排、`OpticalDiscBurner` 光盘刻录编排、进度/结果/异常与安全开关 |

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

### 光盘刻录调用契约

| 平台 | 枚举 | 刻录 |
| --- | --- | --- |
| Windows | 进程内 `Imapi2Interop`（手写 vtable）：`IDiscMaster2` 枚举 + `IDiscFormat2Data` 的 `CurrentPhysicalMediaType`/`CurrentMediaStatus` | `IDiscFormat2Data.Write(stream)` + `ForceMediaToBeClosed`；`IDiscFormat2DataEvents.Update`（dispid 0x200）驱动进度，取消经 `CancelWrite`；ISO 为 2048 字节/扇区，绝不用只收空白 CD-R/RW 的 `MsftDiscFormat2RawCD` |
| Linux | `/sys/class/block/sr*` + `/proc/sys/dev/cdrom/info`（`Can write CD-R:` 等真实字段）作能力，`udevadm info` 作介质状态 | `xorriso -as cdrecord -v -eject dev=/dev/srN blank=as_needed [-multi] <iso>`；`closeSession=false` 用 `-multi` |
| macOS | `drutil list -xml`（`index`/`vendor`/`product`，`index` 即 1 基序号），`drutil -drive N status -xml` 的 `deviceStatus`（`mediaInfo` 下的 `blank`/`appendable`/`overwritable`/`erasable`、`mediaClass`、`mediaType`、`discInfoBlock.discType`） | `drutil -drive N burn [-appendable] -eject <iso>`；`-drive` 为 1 基且必须写在动词前，不存在 `-device`，序号原样取自 `list -xml`，不做 `+1` |

`<iso>` 在刻录前必须通过 `IsoImageValidator`：长度是 2048 的倍数、LBA 16 含 `CD001` 主卷描述符，否则在创建写入器前抛出。

macOS 契约已在真实主机（macOS 27.3 arm64）实测：`drutil help driveselect` 与 `drutil help burn` 明确 `-drive` 取 1 基序号，且 `drutil list -xml` 的 `device` 元素本身从 `index="1"` 起编号；`drutil -drive 1 status` 返回 0 而 `-drive 0`/`-drive 2` 均报 “Could not find a valid device”（退出码 1）。两种 XML 输出都带内部子集缺根元素名的 `DOCTYPE`，严格解析器会拒绝，扫描器与写入器解析前先剥离文件序言（2026-11-20 复测主机无光驱，`deviceList` 为空）。Linux 契约已在真实主机（Armbian trixie aarch64、无光驱）实测：xorriso 1.5.6 的 `-as cdrecord` 支持 `dev=`/`blank=as_needed`/`-eject`/`-multi`/`-v`，`-scanbus` 不支持，无驱动器时报 `Cannot acquire drive` 并退出码 5。Windows IMAPI2 的 CLSID/IID/vtable 槽位与 `ThreadingModel` 逐项对照本机 Windows SDK 10.0.26100.0 头文件与注册表核实，扫描路径在无光驱主机实测通过；三台主机均未执行过真实光盘刻录。

## 已知限制

- Windows 只自动锁定 `<盘符>:\` 形式的挂载点；目录挂载点会直接拒绝并要求手动卸载。
- Linux 在无法把 `/` 映射回物理盘时（例如 LVM/overlay 根）把所有非可移动盘标记为系统盘，优先保证不误擦启动盘。
- 压缩流的解压后大小未知，容量检查按磁盘上的文件大小估算；写阶段的百分比可能为空，校验阶段仍提供百分比与剩余时间。
- ZIP 镜像必须恰好包含一个文件条目，多条目归档直接拒绝。
- 不支持 `.dmg` 与 `.etch` 容器。
- Linux 光驱介质可写性依赖 `udevadm info`（`ID_CDROM_MEDIA_*`）；无 udev 或无法判定时 `IsDiscWritable=false`，拒绝刻录而不冒险写盘。
- 光盘刻录按会话写入，不提供裸设备逐字节回读校验；启用写入器默认的设备端校验（macOS `drutil burn` 默认 verify、Windows IMAPI2 内建，经 `Update` 事件的 `VERIFYING` 阶段上报为 `Verifying` 进度）。
- Windows 刻录取消经 `IDiscFormat2Data.CancelWrite` 协作停止，刻录机完成当前缓冲后留下可续写的未关闭会话；未做强制终止。
- Windows 进度百分比、速度与剩余时间由 `Update` 事件的 `LastWrittenLba` 映射（校验阶段用引擎估算）；Linux `xorriso` 与 macOS `drutil burn` 仍只有阶段级进度。
- macOS 只能按 1 基序号选中刻录机：`drutil` 没有 `-device`，序号之外的稳定标识（`drutil info -xml` 的 `serialNumber`/`ioRegistryEntryPath`）无法传给 `burn`。扫描与刻录前都会重新解析 `list -xml`，但枚举顺序若发生变化仍可能指向另一台刻录机。
- macOS 用 `drutil:index`（即 1 基序号）标识刻录机，扫描与刻录前都重新解析 `list -xml`，避免光驱重新枚举后按过期序号烧错设备。

## 验证命令

```powershell
dotnet build "UotanStudio.DD\UotanStudio.DD.csproj"
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.DD --minimum-expected-tests 1
```
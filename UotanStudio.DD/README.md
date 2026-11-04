# UotanStudio.DD

跨平台磁盘镜像烧录支持库，提供镜像探测、驱动扫描、整盘写入与回读校验。

## 能力

- 探测 `.img`、`.iso`、`.bin`、`.raw`、`.dsk`、`.wic`、`.hddimg`、`.sdcard`、`.rpi-sdimg` 以及 gzip/xz/bzip2/zstd/lz4/lzma/lzop/brotli/zlib/deflate 压缩流和单条目 ZIP。
- Windows、Linux、macOS 驱动扫描，输出裸设备路径、容量、可移动、写保护与系统盘标记。
- 驱动兼容性判定：写保护、容量不足、系统盘、大容量盘、源镜像所在盘。
- 单遍源读取、多目标扇出写入，写入后逐字节回读校验，SHA-256 结果摘要。
- 阶段化进度：准备 / 写入 / 校验 / 完成，包含字节数、百分比、速度与剩余时间。
- 安全确认、系统盘拒绝与 `CancellationToken` 贯穿。

## 快速使用

```csharp
using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;

var image = ImageProbe.Probe(@"D:\images\system.img");
var drives = await DriveScanner.CreateDefault().ScanAsync();

var flasher = new ImageFlasher();
var result = await flasher.FlashAsync(
    image,
    drives.Where(drive => drive.IsRemovable).ToArray(),
    new FlashOptions { ConfirmOverwrite = true },
    progress: new Progress<FlashProgress>(value =>
        Console.WriteLine($"{value.Stage}: {value.Percentage:F1}%")));

Console.WriteLine($"{result.BytesWritten} bytes, sha256={result.SourceChecksum}");
```

## 平台要求

- Windows：管理员权限，写入前自动锁定并卸载目标卷。
- Linux：root 权限，写入前 `umount2` 卸载挂载点。
- macOS：root 权限，写入前执行 `diskutil unmountDisk`。

真实的平台扫描与裸设备写入未在自动化测试中执行；测试覆盖镜像探测、兼容性策略、文件目标写入、校验失败、取消与压缩镜像解码。

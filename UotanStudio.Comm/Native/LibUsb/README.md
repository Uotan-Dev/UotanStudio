# libusb 原生运行库

每个文件均与来源执行 SHA-256
比对；本目录的 `SHA256SUMS` 记录引入时的精确文件身份。macOS 目录使用 .NET RID 的
`osx-*` 命名（来源目录名为 `macos-*`），二进制内容一致。

包含 Windows x64/ARM64、macOS x64/ARM64、Linux x64/ARM64/RISC-V64/LoongArch64
八组依赖。Linux 保留 `libusb-1.0.so` 和 `libusb-1.0.so.0` 两个文件。
这些是平台原生二进制，仍依赖各平台系统运行库；Linux 文件不是 musl 构建。

`UotanStudio.Comm.csproj` 按显式 `RuntimeIdentifier` 选择对应目录；普通无 RID
构建使用 `NETCoreSdkRuntimeIdentifier`，显式 `PlatformTarget` 可覆盖主机架构。
选中的文件作为 Content 使用 `CopyToOutputDirectory=Always` 和
`CopyToPublishDirectory=Always`，直接放在程序目录，不保留源码子目录。
项目引用会传递这些内容文件，消费者无需手工复制 DLL 或设置绝对加载路径。
不用 `PreserveNewest` 的原因：各 RID 目录里的文件共享同一次 checkout 的
LastWriteTime，把 RID 切换到复用过的输出/发布目录时源与目标时间戳相等，
复制会被跳过，留下旧架构的库与二进制并存（表现为 macOS 上 x64 应用加载到
arm64 的 `libusb-1.0.dylib`）。Always 让切换自愈；NuGet 运行时包的原生库
不受本项控制，切换 RID 发布时仍建议使用干净的输出目录。

Comm 和应用层 UotanStudio 都设置 `IsRidAgnostic=false`，使 Desktop 的目标 RID
能穿过项目引用到达 Comm，避免在 Windows x64 主机发布 ARM64 时复制错误架构。
在 Comm 与宿主之间插入的中间引用项目同样需要保留 RID 传递。不支持的 RID 会给出构建警告，
此时 libusb 后端保持禁用，除非把兼容的 libusb 文件放在程序目录，或通过
`CommKit.SetLibusbLibraryPath` / `LibUsbRuntime.SetLibraryPath` 注册其绝对路径；
仅在系统路径安装 libusb 不会启用后端。显式注册的路径优先于程序目录中的文件，可用于系统安装位置。

Windows 使用上游 release/MSYS2，macOS 使用
Homebrew，Linux 使用 Debian/Ubuntu。不同平台来源版本可能不同，不能仅根据
脚本的默认版本声称所有文件版本一致。

libusb 上游为 <https://github.com/libusb/libusb>，采用 LGPL-2.1-or-later。
`COPYING` 来自上游 v1.0.30，构建和发布时以 `libusb-LICENSE.txt` 随程序复制。

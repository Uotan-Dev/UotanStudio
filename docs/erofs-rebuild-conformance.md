# EROFS 修改重建的参数继承与原生互操作验证

验证日期：2026-09-13。

## 结论与使用边界

未修改解包目录时，`FileSystemPreservation` 仍从经过 SHA-256 校验的保真快照精确恢复原镜像，包括原有布局与尾部字节。修改目录后必须重新构建文件系统；本次补齐该路径的压缩配置和 inode 参数继承，避免原始 LZMA、DEFLATE、Zstd 或未压缩镜像被默认重建为 LZ4。

“保持参数”不等于修改后逐字节复现。EROFS 没有保存编码器名称、版本、压缩等级或完整 mkfs 命令行；LZ4 与 LZ4HC 在盘上均为算法 0。不能从镜像可靠恢复这些信息。修改后的数据分配、NID、索引打包、共享 xattr 表、压缩簇边界、文件系统块数和最终镜像大小会重新计算。块地址、feature 位和 checksum 必须描述实际生成的布局，不能直接复制原超级块。

## 参数覆盖

| 参数 | 修改后重建行为 |
| --- | --- |
| 文件系统块大小 | 继承源值；实际覆盖 512、1024、4096 字节，修复小块超级块校验范围 |
| UUID、卷名 | 继承；卷名保存原始 16 字节，包括 UTF-8 标签。用户更改卷名时重新编码并计算校验和 |
| 超级块 epoch、纳秒、build_time 偏移 | 继承；紧凑 inode 的时间为 epoch 加相对秒数，扩展 inode 保留绝对秒数和纳秒 |
| 权限、UID/GID、设备号、硬链接、软链接、二进制 xattr | 沿用保真快照与 `FileSystemEditPlan`，本次原生检查覆盖这些语义；新增条目继续使用已有属性继承规则 |
| 未压缩文件 | 保持未压缩策略，不因整个镜像启用压缩而自动改用 LZ4 |
| LZ4、MicroLZMA、DEFLATE、Zstd | 从源 inode 及压缩配置继承；支持同一镜像不同文件采用不同算法 |
| LZ4 最大回溯距离、最大物理簇大小 | 正确区分旧式超级块 union 与 COMPR_CFGS 记录，继承上限；编码输入限制保证不会产生超出声明距离的匹配 |
| MicroLZMA 字典大小 | 继承；包括非二次幂字典。字典范围按 EROFS 上限限制为 4 KiB 至 8 MiB |
| DEFLATE / Zstd 窗口 | 继承。SharpZipLib 的匹配窗口固定为 32 KiB，较小 DEFLATE 窗口使用不带回溯引用的 Huffman 编码，压缩率会改变 |
| 完整 / 紧凑压缩索引、逻辑簇大小 | 按原文件继承；紧凑索引内部 2B/4B 分组布局重新生成 |
| 普通尾部内联、压缩尾部内联 | 按源策略重建；内联内容必须完整落在一个元数据块内，空间不足的普通文件尾部转为外部块 |
| fragments / dedupe / metabox | 继承可支持的源开关并重新分配；保留数据语义，不承诺原打包 inode、去重簇或共享表的位置一致 |
| 超级块 checksum 开关 | 继承；开启时依据实际字节重新计算 CRC32C |
| 压缩等级、LZ4 vs LZ4HC、mkfs `-C` 的非 LZ4 原始命令行值 | 盘上缺少可恢复信息；不伪造原值。编码等级继续采用现有构建器默认值，非 LZ4 物理簇使用构建器策略 |

`configuration.compression` 现在记录可读的算法配置和上述不可恢复信息；`compressionLevel` 明确为 null。真正重建时重新检查快照哈希并从快照解析参数，旧版保真目录无需依靠新增 JSON 字段也能使用本次修复。原有配置修改保护、发布前一致性检查和原子输出路径继续生效。Sparse 包装 EROFS 的修改重建也调用同一路径。

## 经上游或原生工具证实并修复的问题

1. 修改重建只传入块大小、UUID 和卷名，压缩算法、字典、窗口和 inode 时间戳被默认值覆盖。
2. 未置 COMPR_CFGS 时，超级块的 `available_compr_algs` 位置实际保存 `lz4_max_distance`。先前新建 LZ4 镜像在这个位置写入算法位图 1；现在写入正式 LZ4 配置记录，并按 feature 位解释输入。
3. 紧凑 inode 的 offset 12 是相对 epoch 的 `i_mtime`，不能写入当前绝对秒数；否则原生读取的时间会加两次 epoch。扩展 inode 的纳秒也不能丢弃。
4. 小块镜像 checksum 区间不是 `blockSize - 1024` 一律成立：块大小不超过 1024 时，校验长度为一个完整文件系统块。
5. 连续单逻辑簇的 big-pcluster 紧凑索引在新的 8 字节组中少推进一个物理块，导致原生读取错块。
6. 压缩 map header 必须在 inode 与 xattr 后按 8 字节对齐。
7. 上游压缩尾部内联内容位于索引末尾，仍须解压。旧代码从 map header 后直接复制并补零，并遗漏带特殊 EOF 索引的尾部。现在按上游计算最后索引组的位置，并正确处理压缩或普通内联尾部；构建器也能重建压缩尾部内联。
8. whole-file fragment 的偏移使用 map header 的低 63 位，bit 63 是标记；旧读端错误清除了低位 bit 7，写端还为此人为插入对齐空洞。现按 64 位字段解析和写入。
9. packed inode 本身可以压缩。现在将其按簇解码到自动删除的临时文件，共享读取该文件的所需区间，并拒绝递归引用。处理普通压缩 inode 的尾部 fragment 时也使用此路径。
10. 不能因为文件首块不可压缩就放弃整文件压缩。现在允许同一文件交替存放普通逻辑块与压缩簇，并支持大于一个物理块的压缩簇。
11. 恢复 xattr 后，原本接近一个块大小的文件尾部可能放不下。真实 odm.img 的 `etc/init/mslgservice.rc` 重建时为 4034 字节，加上 32 字节 inode 和 56 字节 xattr 后跨块，被 fsck 拒绝；现回退为外部数据块。目录采用相同的容量约束。

新增读取保持有限缓冲：压缩配置记录限制 4096 字节，物理压缩簇限制 1 MiB、解码簇限制 12 MiB；压缩 packed inode 临时文件限制 64 GiB，区间复制使用 64 KiB 缓冲。没有引入整镜像托管数组。

## 上游依据

- erofs-utils v1.9.4，提交 `f36cadb5c563995ab3aa8572a60ed6b721b9557d`：[include/erofs_fs.h](https://git.kernel.org/pub/scm/linux/kernel/git/xiang/erofs-utils.git/tree/include/erofs_fs.h?h=v1.9.4)，定义超级块 union、inode 时间、压缩配置和碎片布局。
- 同版本 [lib/namei.c](https://git.kernel.org/pub/scm/linux/kernel/git/xiang/erofs-utils.git/tree/lib/namei.c?h=v1.9.4)、[lib/super.c](https://git.kernel.org/pub/scm/linux/kernel/git/xiang/erofs-utils.git/tree/lib/super.c?h=v1.9.4)、[lib/zmap.c](https://git.kernel.org/pub/scm/linux/kernel/git/xiang/erofs-utils.git/tree/lib/zmap.c?h=v1.9.4)、[lib/decompress.c](https://git.kernel.org/pub/scm/linux/kernel/git/xiang/erofs-utils.git/tree/lib/decompress.c?h=v1.9.4)，分别用于时间解释、checksum、索引/内联映射和解压配置对照。
- 混合算法源图使用 erofs-utils v1.8.10，提交 `51b5939b5f783221310d25146e6a2019ba8129b6`。本机 v1.9.4 的 `--compress-hints` 三列路径在 `erofs_load_compress_hints` 解引用 importer 参数时发生段错误，经 gdb 定位；没有修改上游源码绕过此错误。所有重建镜像仍由未修改的 v1.9.4 fsck / FUSE 检查。

## 验证环境与矩阵

Alpine WSL2：`6.18.33.2-microsoft-standard-WSL2`。按本次授权使用免密 sudo 安装编译依赖、加载已有 EROFS 模块，独立编译工具位于 `/tmp/uotan-erofs-tools` 和 `/tmp/uotan-erofs-tools-1.8.10`；没有替换发行版自带二进制。

该 WSL2 内核启用了 EROFS LZ4，但未编入 LZMA、DEFLATE、Zstd。系统 erofs-utils 1.9.4 只启用 LZ4 / DEFLATE。因此分别验证以下层次，不能把 FUSE 或 QEMU 结果称为这些算法已在当前 WSL2 内核原生支持：

| 层次 | 检查 |
| --- | --- |
| 上游制图 → 项目解包 → 不修改重打包 | 输出 SHA-256 与输入完全相同 |
| 上游制图 → 修改现有文件、增加文件和空目录 → 项目重建 | 逐项检查超级块身份、压缩配置、权限、UID/GID、时间、xattr、软硬链接 |
| Alpine 原生 fsck.erofs 1.9.4 | `--extract` 完整解码并对每个普通文件核对 SHA-256，使用 `--xattrs` 验证扩展属性 |
| Alpine WSL2 原生内核挂载 | 未压缩与 LZ4 系列，只读 loop mount、全部文件哈希、目录、链接、权限、xattr、卸载和镜像哈希不变 |
| Alpine FUSE 挂载 | LZMA、DEFLATE、Zstd、混合算法，执行相同内容与元数据检查，显式 `-o ro` |
| Alpine 内运行的独立 QEMU Linux 6.18.48 内核 | 四种压缩算法及布局组合，所有镜像只读 virtio 块设备挂载，核对完整目录与文件 SHA-256，卸载并复查镜像哈希 |

参数化案例包括：未压缩、LZ4、LZ4HC 12 / 16 KiB 最大物理簇、MicroLZMA 1 MiB 字典、DEFLATE、Zstd 64 KiB 字典、LZ4 512 字节块、未压缩 1024 字节块、完整索引、四算法混合、fragments/dedupe、ztailpacking、独立秒/纳秒时间、4 KiB DEFLATE 窗口、3 MiB MicroLZMA 字典、中文卷名。数据包括重复文本、不可压随机数据、重复的 8 KiB 随机片段、跨块尾部、空文件、空目录、软硬链接、FIFO、高 UID/GID 和共享 xattr。

独立 QEMU 环境沿用仓库的 `FirmwareMountCheck` 脚本；内核、模块和每个被挂载镜像的哈希都保存在本次产物目录的 `qemu/logs/` 中。

最终结果：16 个参数化重建案例加 1 个真实镜像案例全部通过；EROFS、文件系统保真/编辑和重打包配置相关测试合计 140 项，通过 140、失败 0、跳过 0。QEMU 内核矩阵 16 项全部通过，每个镜像验证 10 个普通文件的 SHA-256、完整目录和成功卸载，检查前后镜像哈希相同。另用 Alpine 系统自带 `/usr/bin/fsck.erofs --extract` 检查其支持的 13 个样本，全部通过。Desktop Debug 构建成功，0 警告、0 错误；工作区 diff 检查通过。

## 真实 odm.img

- 源文件：`D:\Firmware\odm.img`，509517824 字节，SHA-256 `ce70f3183b64e4a1a1c683a6757d00372483e36d19584ccedb32a453ecc81f15`。
- 原始文件系统：4096 字节块，74 个条目，旧式 LZ4 参数，最大回溯距离 65535，含 shared xattr，超级块 xattr block address 为 0。
- 测试只在解包副本的 `etc/build.prop` 追加一行标记，然后重建。原镜像与重建镜像同时在 WSL2 内核只读挂载，通过原生 Python 对全部 74 个条目核对类型、权限、UID/GID、mtime 纳秒、设备号、xattr、链接关系和 SHA-256；仅指定文件采用修改后的预期内容。原镜像哈希保持不变。
- 当前重建大小 536940544 字节，比原镜像增加约 5.38%。这体现了“保持可解释参数”和“复现原编码器压缩率”的区别：该结果通过 fsck 与 mount，但不能据此宣称仍适合原有固定分区容量。文件系统块数与最终大小随实际布局重新计算。
- 重建 SHA-256：`9418c3ecd66b1cce089bbe489d846f8dadf49bd487edb1a5e05468407e18f85b`。

完整测试、原生工具日志、QEMU 日志与重建样本保留于 `D:\Temp\uotan-erofs-validation-20260913`。这里的文件系统验证不代表任意 EROFS 新特性组合或所有设备固件启动均通过；例如压缩目录、压缩 metabox、同 inode 的多个压缩算法分支和外部设备拓扑不在本次重建互操作矩阵中。

## 重跑

测试不安装软件、不修改 sudoers、不自动加载模块。普通环境缺少依赖时明确跳过；本次运行先按用户授权准备环境。

```powershell
$env:UOTAN_TEST_WSL_DISTRO = 'Alpine'
$env:UOTAN_EROFS_TOOLS = '/tmp/uotan-erofs-tools'
$env:UOTAN_EROFS_MIXED_MKFS = '/tmp/uotan-erofs-tools-1.8.10/bin/mkfs.erofs'
$env:UOTAN_EROFS_ARTIFACTS = 'D:\Temp\uotan-erofs-validation-20260913'
dotnet build UotanStudio.Tests/UotanStudio.Tests.csproj -c Debug
wsl -d Alpine --exec sudo -n modprobe erofs
& UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe `
    -class 'UotanStudio.Tests.Firmware.ErofsRebuildInteropTests'
```

只有混合算法夹具需要旧版 mkfs；它的读取校验仍使用 `UOTAN_EROFS_TOOLS` 指向的完整版 v1.9.4。保真和配置回归还应运行 `Erofs*`、`FileSystemPreservationTests`、`FileSystemEditing*`、`RepackMetadata*` 测试。

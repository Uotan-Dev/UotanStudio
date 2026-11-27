# UotanStudio.Assets/Assets

随包二进制资源的唯一存放点。每个 bundle 一个子目录，原始负载与同名的压缩帧 `<path>.zst` 并排存放，`manifest.tsv` 是覆盖全部 bundle 的唯一清单；两者都以 `EmbeddedResource` 进 `UotanStudio.Assets` 程序集，逻辑资源名分别是 `assets/<bundle>/<path>.zst` 与 `assets/manifest.tsv`。

## 来源与许可证索引

| 目录 | 负载 | 来源与许可 |
| --- | --- | --- |
| `fonts/` | `MiSansVF.ttf`、`JetBrainsMonoVF.ttf`、`JetBrainsMonoItalicVF.ttf` | MiSans 可变字形（`wght` 轴 150–700）与 JetBrains Mono v2.304 正/斜体可变字形（`wght` 轴 100–800，SIL OFL 1.1），许可见 `fonts/NOTICE.md`；原始文件取自 `reference/resource/fonts-source/` |
| `ohos/device/` | `libscrcpy_server*.z.so`、`uitest_agent*.so` | DevEco Studio 随附的设备端原生文件，来源、版本与许可见 `ohos/README.md`；提取脚本 `tools/deveco-mirroring/extract.ps1` |
| `drivers/rockusb/` | `rockusb.inf`、`rockusb.cat`、`rockusb.sys` | 厂商 Rockusb 驱动包，来源 URL、签名与许可见 `reference/driver/driver-pkg/README.md` |
| `drivers/mediatek-vcom/`、`drivers/mediatek-full/` | 联发科 `cdc-acm`（VCOM）与 ADB 的 INF/CAT/SYS 及 amd64 coinstaller | 微软更新目录分发的 MediaTek WHQL 包，随包范围与排除项见 `reference/driver/driver-pkg/README.md` 的打包范围一节 |
| `drivers/google-usb/`、`drivers/rockchip-adb/` | `android_winusb.inf`、两份 `.cat`；rockchip 另带 `NOTICE.txt` | Google USB Driver r13 与 Rockchip DriverAssistant 的 ADB 包；两包不再各存一份 amd64 coinstaller，改由 `drivers/winusb-coinstaller/` 共享 |
| `drivers/winusb-coinstaller/` | `amd64__WdfCoInstaller01009.dll`、`amd64__winusbcoinstaller2.dll` | Google USB Driver r13 的 amd64 coinstaller；原样逐字节重复于多个 ADB 包，故只随包一份，由 `DriverPackageAssets` 在物化时合并 |
| `drivers/qualcomm-qud/` | 高通 QUD 全家桶 INF/CAT/SYS，按 `__` 编码目录 | 高通 QUD 官方签名包 |
| `drivers/unisoc-spreadtrum/` | 紫光展锐 `sprdadb`/`sprdvcom`/`sprdvmdm`/`rdavcom` 的 INF+CAT、amd64 `.sys` 与 coinstaller，按 `__` 编码目录 | 紫光展锐官方 Windows 驱动包，来源与随包范围见 `reference/driver/driver-pkg/README.md`；该包不含 `android_winusb.inf`，故自备 coinstaller 而不依赖共享包 |
| `delivery/` | `update-binary-arm64` | 固定版本的原生 arm64 Edify 更新器，用途、卡刷调用与设备侧边界见 `UotanStudio.Pipeline/README.md` |
| `firmware/magic/` | `CompiledMagicData.zst` | 由 `UotanStudio.Firmware.MagicGen` 从 vendored magic(5) 语料生成，语料来源与 BSD-4-Clause 许可见 `UotanStudio.Firmware.MagicGen/corpus/README.md` |
| `scrcpy/` | `scrcpy-server.jar`（可选） | 仓库不随包该二进制；目录为空时 `scrcpy` bundle 未随包，`AssetCatalog.ScrcpyServer.IsPresent` 为 `false` |

## 原始负载与压缩帧

- `drivers/` 下的全部帧只在 Windows 目标里以 `EmbeddedResource` 嵌入（csproj 按 `RuntimeIdentifier` 分块）：没有 RID 的本地构建与测试宿主保留它们，`linux-*`、`osx-*` 与 `linux-musl-*` 发布则整块排除，帧文件仍留在版本库供 Windows 发布与清单校验使用。
- USB 转 UART 桥接芯片（Silicon Labs CP210x、FTDI CDM）与 WCH 一样不随包：它们从不构成固件操作的前置条件，标准 CDC ACM 设备由 Windows 内置 `usbser` 覆盖。原始包仍留在 `reference/driver/driver-pkg/` 供日后按需接入。
- 原始负载（`.so`、`.inf`/`.cat`/`.sys`、`update-binary-arm64`）与压缩帧放在同一 bundle 目录；字体的原始 `.ttf` 留在 `reference/resource/fonts-source/`，脚本的 `--fonts-source` 默认读取该目录。驱动包的原始负载留在 `reference/driver/driver-pkg/`（含 zip 归档），帧由脚本每包一张显式文件表生成，因此 `drivers/` 下只提交帧。原始负载只是生成输入，不内嵌，也不得再被任何项目直接作为 `EmbeddedResource` 引用。
- 帧按 zstd level 19 压缩，脚本每次按原始负载重写；原始负载被删除时，非 `--check` 运行会删掉对应的孤立帧。`generated` 来源的帧不重压缩，只被索引。
- 帧与原始负载都进版本库；不要把 `%TEMP%` 下的物化副本、调试输出或脚本临时产物放回本目录。

## manifest.tsv

- 由 `dotnet run tools/compress-embedded-assets.cs` 生成，文件头两行是生成器注释，正文六列制表符分隔：`bundle`、`path`、解压长度、小写 SHA-256、编码（`zstd`）、`origin`（`source` 或 `generated`），按 `bundle` 与 `path` 序数排序。
- 不得手改：改完原始负载后重跑脚本，`dotnet run tools/compress-embedded-assets.cs -- --check` 只校验不写盘，并审查差异。
- 清单一行的长度与 SHA-256 必须与 `<bundle>/<path>.zst` 解出的字节完全一致；运行期由 `AssetStore` 校验，不一致直接失败。

资源布局、命名规则与新增 bundle 的流程见上一级 `AGENTS.md`，公共 API 与缓存布局见 `../../README.md`。
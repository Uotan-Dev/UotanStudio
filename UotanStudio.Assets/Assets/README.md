# UotanStudio.Assets/Assets

随包二进制资源的唯一存放点。每个 bundle 一个子目录，原始负载与同名的压缩帧 `<path>.zst` 并排存放，`manifest.tsv` 是覆盖全部 bundle 的唯一清单；两者都以 `EmbeddedResource` 进 `UotanStudio.Assets` 程序集，逻辑资源名分别是 `assets/<bundle>/<path>.zst` 与 `assets/manifest.tsv`。

## 来源与许可证索引

| 目录 | 负载 | 来源与许可 |
| --- | --- | --- |
| `fonts/` | `MiSans-Regular.ttf`、`MiSans-Bold.ttf` | MiSans 字形，许可见 `fonts/NOTICE.md`；原始文件取自 `reference/fonts-source/` |
| `ohos/device/` | `libscrcpy_server*.z.so`、`uitest_agent*.so` | DevEco Studio 随附的设备端原生文件，来源、版本与许可见 `ohos/README.md`；提取脚本 `tools/deveco-mirroring/extract.ps1` |
| `drivers/rockusb/` | `rockusb.inf`、`rockusb.cat`、`rockusb.sys` | 厂商 Rockusb 驱动包，来源 URL、签名与许可见 `reference/driver-pkg/README.md` |
| `drivers/mediatek-vcom/`、`drivers/mediatek-full/` | 联发科 `cdc-acm`、MBIM、WPD 与 ADB 的 INF/CAT/SYS 及 amd64 coinstaller | 微软更新目录分发的 MediaTek WHQL 包，随包范围与排除项见 `reference/driver-pkg/README.md` 的打包范围一节 |
| `drivers/google-usb/`、`drivers/rockchip-adb/` | `android_winusb.inf`、两份 `.cat`、amd64 coinstaller；rockchip 另带 `NOTICE.txt` | Google USB Driver r13 与 Rockchip DriverAssistant 的 ADB 包，只随 amd64 coinstaller |
| `drivers/qualcomm-qud/`、`drivers/silabs-cp210x/`、`drivers/ftdi-cdm/` | 各厂商 INF/CAT/SYS，按 `__` 编码目录 | 高通 QUD、Silicon Labs CP210x 与 FTDI CDM 官方包，silabs 另带许可文本 |
| `delivery/` | `update-binary-arm64` | 固定版本的原生 arm64 Edify 更新器，用途、卡刷调用与设备侧边界见 `UotanStudio.Delivery/README.md` |
| `firmware/magic/` | `CompiledMagicData.zst` | 由 `UotanStudio.Firmware.MagicGen` 从 vendored magic(5) 语料生成，语料来源与 BSD-4-Clause 许可见 `UotanStudio.Firmware.MagicGen/corpus/README.md` |
| `scrcpy/` | `scrcpy-server.jar`（可选） | 仓库不随包该二进制；目录为空时 `scrcpy` bundle 未随包，`AssetCatalog.ScrcpyServer.IsPresent` 为 `false` |

## 原始负载与压缩帧

- 原始负载（`.so`、`.inf`/`.cat`/`.sys`、`update-binary-arm64`）与压缩帧放在同一 bundle 目录；字体的原始 `.ttf` 留在 `reference/fonts-source/`，脚本的 `--fonts-source` 默认读取该目录。驱动包的原始负载留在 `reference/driver-pkg/`（含 zip 归档），帧由脚本每包一张显式文件表生成，因此 `drivers/` 下只提交帧。原始负载只是生成输入，不内嵌，也不得再被任何项目直接作为 `EmbeddedResource` 引用。
- 帧按 zstd level 19 压缩，脚本每次按原始负载重写；原始负载被删除时，非 `--check` 运行会删掉对应的孤立帧。`generated` 来源的帧不重压缩，只被索引。
- 帧与原始负载都进版本库；不要把 `%TEMP%` 下的物化副本、调试输出或脚本临时产物放回本目录。

## manifest.tsv

- 由 `dotnet run tools/compress-embedded-assets.cs` 生成，文件头两行是生成器注释，正文六列制表符分隔：`bundle`、`path`、解压长度、小写 SHA-256、编码（`zstd`）、`origin`（`source` 或 `generated`），按 `bundle` 与 `path` 序数排序。
- 不得手改：改完原始负载后重跑脚本，`dotnet run tools/compress-embedded-assets.cs -- --check` 只校验不写盘，并审查差异。
- 清单一行的长度与 SHA-256 必须与 `<bundle>/<path>.zst` 解出的字节完全一致；运行期由 `AssetStore` 校验，不一致直接失败。

资源布局、命名规则与新增 bundle 的流程见上一级 `AGENTS.md`，公共 API 与缓存布局见 `../../README.md`。
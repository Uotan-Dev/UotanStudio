# OpenHarmony 投屏设备端运行时

本目录的 `device/` 是 UotanStudio 的 HDC 投屏在设备侧使用的二进制运行时：UI Test 控制代理与
`libscrcpy_server*.z.so` 采集库。它们由 `OhscrcpyServerManager` 在会话建立时按 UI Test 版本与
CPU 架构选择，并通过托管 HDC 会话推送到 `/data/local/tmp`。

## 来源

- 本机 DevEco Studio：`26.0.0.821`（build `261.23567.138.36.2600821`）
- 插件：`plugins/screen-mirroring/lib/hosScrcpy-1.0.20-beta.jar`
- JAR SHA-256：`62cba3701b635a9d9c906a70f18cbc43efd60b7bfd61e2bb18fb88b2c7dfb187`
- 提取方式：`tools/deveco-mirroring/extract.ps1`（默认读取 `C:\Program Files\Huawei\DevEco Studio`）

这些文件与 `reference/HdcMirror/tools/runtime/device/` 中的同名文件逐字节一致；以 DevEco 安装包为权威来源。
`libscrcpy_server0.z.so` 作为最后的旧版回退候选一并保留，其余 `libscrcpy_server*.z.so` 按文件名中的版本
从新到旧尝试。`uitest_agent_x86_1.1.12.so` 供 `x86_64` 设备使用。

## 打包与运行时布局

原始 `.so` 文件保留在本目录作为生成输入，不内嵌。构建时由 `tools/compress-embedded-assets.cs` 生成
`device/<文件名>.zst`，并把对应行写进 `UotanStudio.Assets/Assets/manifest.tsv`；压缩帧内嵌进
`UotanStudio.Assets` 程序集，逻辑资源名是 `assets/ohos/device/<文件名>.zst`。

运行时由 `AssetStore.Materialize(AssetCatalog.OhosDevice, cancellationToken)` 把 `ohos/device` 资源包物化到
`%TEMP%/UotanStudio/assets/ohos/device/<bundle 摘要前 16 位>/`；每个帧的长度与 SHA-256 由 `AssetStore` 按清单
校验，写入经同目录临时文件加原子替换，同一 bundle 摘要的目录后续直接复用。采集库与 UI Test 代理的选择、推送
仍按真实文件路径进行。

`ohos/device` 在共享清单里的行是六列 `bundle`/`path`/长度/SHA-256/编码/来源，`path` 列就是本目录的纯文件名。

## 文件与摘要

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `libscrcpy_server0.z.so` | 3,029,173 | `09468185a89a141f23484d73f4c2d3f140604dddae2e52f4663eeadce4834bb4` |
| `libscrcpy_server1.z.so` | 3,041,461 | `ec827f420bf7372c7b29fab2d1a72d7df204b147e68ba396a52a537471fa9d8a` |
| `libscrcpy_server2.z.so` | 309,430 | `60da9096f8fd18c36f8bf154858e66a45adc5dd3f40a5698348d1b931a07d1e0` |
| `libscrcpy_server3.z.so` | 288,950 | `2b0cab7e2e158f049b497277fa4106480f527c205b3d7822a002712e1e3d537d` |
| `libscrcpy_server_5.10-20260114.z.so` | 272,567 | `30535ef1d8e1e80b3a67361122a55d144a3a0449552522eca45ad37e969fc2ab` |
| `libscrcpy_server_unix_6.3.1-20260113.z.so` | 272,567 | `3e29e3a1d23ea6dbd4e6fb3b8f094ca5d4b15b4c580f421fba3d0e1f5429e3fe` |
| `libscrcpy_server_unix_6.4-20260113.z.so` | 264,374 | `b698b41ae1351337eeea3dcc87bf0160552196d669ba321b4963c342fa5a47f4` |
| `libscrcpy_server_unix_6.5-20260313.z.so` | 268,469 | `2c8f1ef94d581281bf8e86d54f5939af9b205d5db5aa2cfb27f2562215c87b0a` |
| `libscrcpy_server_unix_6.8-20260801.z.so` | 247,989 | `05ba3a6893bf518ff7e01ef6dc9ca96a325db2374e36dce80ff22eb179fbc458` |
| `libscrcpy_server_unix_6.9-20260801.z.so` | 239,799 | `296e3b0f0ba92c176639e946174a0bb9096025f313fa11bcb27628e6aca5612b` |
| `uitest_agent_1.1.3.so` | 149,686 | `aa9b11f0bf7d2118f3fa323f1bf3961c19ebe12bdf544a90dc0f6ccdd72b482b` |
| `uitest_agent_1.1.5.so` | 153,782 | `bae4d731f0ffdb6fe9079083a387f142a55cb7c7faaf5550185fc055f31e1d6a` |
| `uitest_agent_1.1.12.so` | 600,247 | `ae00607958c2e73624319b3066dc427111b2b8224fb292909c8563f686a6ab2c` |
| `uitest_agent_1.2.3.so` | 600,246 | `5dfbaca7e274a85bece8f225148ceb759b94d7319b956b9a7ea40d37944e4a9b` |
| `uitest_agent_x86_1.1.12.so` | 1,465,286 | `b7aee90fa38ea2ee79b2b91a2add750e27a3e0bb7c3e0dcea0e837a99c9d7563` |

## 许可与再分发

这些是华为 DevEco Studio 随附的设备端原生文件，不是 UotanStudio 的源码，也不在本仓库
`LICENSE.txt` 的授权范围内。它们只作为互操作运行时随应用分发；从 DevEco 升级更新时用同一脚本重新提取，
不要手工改动内容。项目本身不声称对这些二进制拥有版权。
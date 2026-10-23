# DevEco 投屏运行时提取

`extract.ps1` 从本机 DevEco Studio 的 `screen-mirroring` 插件 JAR 中提取 HDC 投屏所需的设备端二进制，
写入 `UotanStudio.Mirroring/Assets/ohos/device/`。这些文件随应用输出与发布到 `ohos/device/`，由
`OhscrcpyServerManager` 在会话建立时按 UI Test 版本与架构选择并推送到设备的 `/data/local/tmp`。

## 使用

```powershell
pwsh -File tools/deveco-mirroring/extract.ps1
```

默认读取：

- DevEco：`C:\Program Files\Huawei\DevEco Studio`
- 插件：`plugins\screen-mirroring\lib\hosScrcpy-1.0.20-beta.jar`
- 输出：`UotanStudio.Mirroring\Assets\ohos\device`

DevEco 安装在其他位置时：

```powershell
pwsh -File tools/deveco-mirroring/extract.ps1 -DevEcoRoot 'D:\Huawei\DevEco Studio'
```

脚本只会覆盖 `libscrcpy/*.so` 与 `uitest_agent*.so` 这 15 个条目，并打印每个文件的大小与 SHA-256；
来源、版本和摘要记录在 `UotanStudio.Mirroring/Assets/ohos/README.md`。

## 边界

- 这些是华为 DevEco 随附的第三方二进制，不是本仓库源码；升级 DevEco 后重新运行脚本，不要手工修改内容。
- DevEco 的 JAR 只包含设备端采集库与 UI Test 代理，不包含设备系统的 `libgrpc.z.so` / `libprotobuf`。
  设备系统 ABI 是否满足采集库由设备固件决定，不由本脚本补齐。
- 脚本不参与构建；仓库中已提交提取后的二进制，离线构建不需要安装 DevEco。

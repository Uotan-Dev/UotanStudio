# UotanStudio.Net 项目指令

本目录是独立的下载、GitHub 镜像与 Release/版本查询叶子库；仓库级规则见根 `AGENTS.md`。

## 边界

- 叶子库：不得引用任何 `UotanStudio.*` 项目，只依赖 BCL；不得把 UI、日志服务、设置状态或设备状态引入本库。
- HTTP 下载、GitHub 镜像枚举、Release 元数据读取与版本归一化只在这里实现；上层（宿主、Delivery）只调用本库公共 API。
- 文件原子发布使用 `IO/AtomicPublish` 的内部实现：调用方在目标同目录写出暂存文件，成功后单次覆盖移动，失败删除暂存；不得引用 Firmware 的 `Core.IO`。
- Release 元数据只读源站 `api.github.com`，镜像只用于下载产物；元数据响应体上限为 1 MiB。
- `GitHubRelease.Version` 的归一化规则固定：去首尾空白；`v`/`V` 后紧跟数字时去掉该前缀；其余文本原样保留（预发布后缀随之保留）。
- JSON 一律走 `GitHubJsonContext` 源生成上下文，禁止反射式序列化或动态类型；所有网络调用贯穿 `CancellationToken`，失败以带状态码的 `HttpRequestException` 或 `InvalidOperationException` 上报。

## 文件布局

| 路径 | 职责 |
| --- | --- |
| `Http/HttpDownload.cs` | 镜像源枚举、测速、单源/多源下载与请求构造 |
| `GitHub/GitHubMirrorSite.cs` | 镜像站点描述 |
| `GitHub/GitHubReleaseClient.cs` | Release 元数据读取、版本归一化与纯 JSON 解析 |
| `GitHub/GitHubRelease.cs` | 公共 Release/Asset 记录与源生成 JSON 上下文 |
| `Components/Scrcpy/` | scrcpy 与 ohscrcpy 的 Release 记录及其资产投影 |
| `IO/AtomicPublish.cs` | 内部同目录原子发布 |

## 验证

```text
dotnet build UotanStudio.Net/UotanStudio.Net.csproj --nologo -c Debug
```
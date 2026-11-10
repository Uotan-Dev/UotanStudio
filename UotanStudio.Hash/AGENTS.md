# UotanStudio.Hash 项目指令

在本目录内计算或新增校验和、摘要前读这份文件。项目定位与算法清单见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本库是最底层叶子库，只依赖 BCL 与 `System.IO.Hashing`，不得引用任何其他业务项目。
- 全仓库生产代码只能经本库公共契约做校验和与摘要计算；其他项目不得 `using System.IO.Hashing`、不得调用 `MD5`/`SHA*`/`IncrementalHash` 的哈希入口，也不得自带 CRC 多项式表。`UotanStudio.Tests/Firmware/Architecture/HashBoundaryGuardTests.cs` 负责全仓扫描。
- 本库是仓库内唯一可以声明 `System.IO.Hashing` 包的项目。签名与验签、作为签名参数的 `HashAlgorithmName`、HMAC、随机数生成、AES 等厂商加解密、定时比较不在本库转发范围。
- `InternalsVisibleTo` 只给 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks`；其他项目通过公共契约消费，不得靠扩大友元程序集绕开文档与边界要求。

## 语义约定

- 与 BCL 语义一致的标准 CRC-32 和 MD5/SHA 摘要族由本库转发；格式规定的非标准初值、寄存器语义、无最终异或、厂商私有变体与加性校验和由本库实现，不能为了“统一”把两者合并。
- `Crc32Ieee` 与 `Crc32Register` 只差一次最终取反，但必须是两个类型；合并会让漏写末异或的改动静默改掉落盘 CRC 而不触发往返测试。
- 续算 API 的第二参数是上一次调用返回的打包值或 seed：`Adler32.Compute(span, previousResult)`、`Crc32Register`、`Crc16Arc`、`RockchipCrc32` 都按这个约定。不要给续算重载加默认值，否则会在单参调用处产生 `CS0121` 二义。
- 标准式流式续算用 `Crc32IeeeAccumulator`，摘要续算用 `Digests` 的 `DigestAccumulator`：`GetHashAndReset()` 定稿并重置，`GetCurrentHash()` 只观察不重置。
- 新增算法要同时加已知答案向量；CRC-16 与 Adler 的向量还应与类内逐位参考循环交叉验证，不能只钉一个常量。

## 文档与验证

- 本库全部类型都是 public，公共类型与成员必须有中英双语 XML 注释；`TreatWarningsAsErrors` 会让 `CS1591` 直接失败。仓库不维护 `PublicAPI.*.txt` 基线。
- 说明性内容写进 XML 注释，不使用行间 `//` 注释。
- 定向验证：

```text
dotnet build UotanStudio.Hash/UotanStudio.Hash.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.HashBoundaryGuardTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Firmware --minimum-expected-tests 1
```
# UotanStudio.Hash

面向 .NET 10 的校验与摘要类库，根命名空间为 `UotanStudio.Hash`，运行时只依赖 `System.IO.Hashing` 与 BCL，是仓库里最底层的叶子库。**全仓库的校验和与摘要计算都经由本库的公共契约**：磁盘格式规定的非标准初值、寄存器语义、无最终异或、厂商私有变体由本库自己实现；语义与 BCL 完全一致的标准式与摘要族由本库转发，转发即硬件加速实现的位置。

## 目录与职责

一个类型独占一个目录时，目录名同时是命名空间最后一段与类型名（`Crc16CcittFalse/Crc16CcittFalse.cs` 声明 `namespace UotanStudio.Hash.Crc16CcittFalse`）；同族多类型集中在一个目录，命名空间为该目录名（`Sums/`、`Digests/`）。

| 类型 | 参数 | 用途 |
| --- | --- | --- |
| `Crc32Ieee` | 反射 0xEDB88320，init 0xFFFFFFFF，**做**最终异或 | ZIP 条目与目录、GPT 头与条目数组、sparse 块、XZ 块校验、LZOP、zucchini、RKDL GPT |
| `Crc32IeeeAccumulator` | 同上，跨块续算，不可播种 | 流式写出或回读校验字节的调用方 |
| `Crc32Register` | 反射 0xEDB88320，寄存器 seed，**无**最终异或 | F2FS 超级块魔数种子、EROFS、AMLogic、PAC |
| `Crc32c` | 反射 0x82F63B78，寄存器 seed，无最终异或 | EROFS、ext4 与 ext4 journal 的 crc32c |
| `Crc16Arc` | 反射 0xA001，带 seed，无末异或 | PAC 头部与数据校验 |
| `Crc16CcittReversed` | 反射 0x8408，init 0xFFFF，末异或 0xFFFF | HDLC 帧 DIAG 报文、展讯 UPDATE.APP 条目与头校验 |
| `Crc16CcittFalse` | 高位优先 0x1021，init 0xFFFF，无末异或 | RKDL loader 下载帧 |
| `RockchipCrc32` | 高位优先 0x04C11DB7，带 seed | RKAF 资源镜像 |
| `XzCrc64` | 反射 0xC96C5795D7870F42，全 1 初值，出值取反 | XZ 容器的 CRC-64 校验字段 |
| `Adler32` | 模 65521，按 5552 字节分块归约 | zlib 风格载荷与 DTBO/DEX 尾部校验 |
| `Xxh32` | erofs-utils 与内核一致的 XXH32 | EROFS xattr 名称过滤器 |
| `F2fsDentryHash`、`HalfMd4Hash` | 半 MD4 家族 | F2FS 目录项哈希与 ext4 HTree 目录哈希 |
| `Sums.ByteSum8` / `ByteSum16` / `ByteSum32` | 逐字节累加，mod 2^8 / 2^16 / 2^32 | HDC 负载、MTK legacy 与 XFlash 帧、ADB 消息、CPIO `070702` 负载、TAR 头块 |
| `Sums.WordSum32LittleEndian` | 小端双字求和后补尾部字节 | MTK preloader 命令校验 |
| `Digests.Md5` / `Sha1` / `Sha256` / `Sha384` / `Sha512` | 转发 `System.Security.Cryptography` | 一次性、目标缓冲区、小写十六进制、同步与异步整流、`CreateAccumulator()` 续算 |

`Crc16CcittReversed` 不声称对应任何目录学名称：不同资料里 “X.25”“CCITT-Reversed” 之类叫法的初值与末异或并不一致，因此类型文档只固定参数串与实测校验值——ASCII `123456789` 得 `0x906E`，空输入得 `0x0000`。`Crc16CcittFalse` 同理：`123456789` 得 `0x29B1`，空输入得 `0xFFFF`，字节 `01..10` 得 `0x0FEF`。

## 标准式与寄存器式是两个类型

`Crc32Ieee` 与 `Crc32Register` 只差一次按位取反：`Crc32Ieee.Compute(d) == Crc32Register.Compute(d, 0xFFFFFFFF)` 的取反结果。两者刻意不合并成一个类型的两个重载，因为写错一次末异或不会让往返测试变红——它只会静默改掉每个落盘的 CRC 字段。`UotanStudio.Tests/Firmware/Crc32SemanticsEquivalenceTests.cs` 直接对照 BCL 钉住这条恒等式与分块续算等价性。

## 可见性边界

本库所有类型都是 public，新增或调整公共成员必须写中英双语 XML 注释（`TreatWarningsAsErrors` 使 `CS1591` 直接失败）。公开 API 形态不受基线文件约束，不再维护 `PublicAPI.*.txt`。`Properties/AssemblyInfo.cs` 的 `InternalsVisibleTo` 只开放给 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks`；其他项目一律通过公共契约消费，不得以扩大 `InternalsVisibleTo` 的方式绕开文档要求。

## 转发边界

`UotanStudio.Hash` 之外不得出现下列调用，由 `UotanStudio.Tests/Firmware/Architecture/HashBoundaryGuardTests.cs` 全仓扫描把关：`System.IO.Hashing` 的 using 与限定调用、`MD5`/`SHA1`/`SHA256`/`SHA384`/`SHA512` 的 `HashData`/`Create`、`IncrementalHash.CreateHash`，以及本地 CRC 多项式表常量。`System.IO.Hashing` 包也只允许在本库的 csproj 中声明。

转发范围限于**摘要与校验和**。不转发的部分留在 BCL：`RSA`/`ECDsa`/`DSA` 签名与验签、作为签名参数传递的 `HashAlgorithmName`、`HMAC*`、`RandomNumberGenerator`、`Aes` 与其它厂商加解密、`CryptographicOperations` 定时比较。边写边算哈希的流包装位于 `UotanStudio.Firmware/Core/Streams/`，不在本库。

按流偏移读取再求和的**覆盖规则**也不进本库：RomFS 的大端双字求和经带边界检查的读取访问器进行，并以“含校验字段的区间求和为 0”判定有效性，属于格式覆盖语义而非可复用的摘要算法，因此在本库之外。

## 续算约定

`Adler32.Compute(ReadOnlySpan<byte>, uint previousResult)` 的第二个参数是**上一次调用返回的打包值**（低半部 `a`、高半部 `b`），空区间原样返回该值；逐块折叠与对拼接后的完整字节调用一次单参重载结果相同。`Crc16Arc`、`Crc32Register` 与 `RockchipCrc32` 采用同一表述方式：把上次结果作为下次的 seed 传入即可续算。这些重载刻意不给默认值，否则会在 `Compute(span)` 处产生 CS0121 二义。

不需要任意种子的标准式续算走 `Crc32IeeeAccumulator`（`Append`/`GetCurrentHash`/`Reset`），摘要续算走 `Digests` 的 `DigestAccumulator`：`GetHashAndReset()` 定稿并重置，`GetCurrentHash()` 只观察不重置（Payload 签名先对 header+manifest 取摘要签名，再继续并入数据区）。

## 校验向量

每个类型的已知答案向量放在消费者所在域：`UotanStudio.Tests/Firmware/RockchipCrc32Tests.cs`、`Crc16CcittReversedTests.cs`、`Crc16CcittFalseTests.cs`、`Adler32ContinuationTests.cs`、`Crc32SemanticsEquivalenceTests.cs`。CRC-16 与 Adler 的向量测试同时用类内逐位参考循环交叉验证查表实现，避免只钉一个常量而让表构建失去检验。

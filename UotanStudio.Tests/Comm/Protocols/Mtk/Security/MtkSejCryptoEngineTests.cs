using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.HwCrypto;
using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

/// <summary>
/// Drives the SEJ engine against a scripted AES register device: the HACC bring-up order, the block
/// run loop addressing, the not-ready abort, the hwcode forced non-legacy bring-up and the
/// <c>seccfg</c> hash resolution/rewrite round trip.
/// <para>用脚本化的 AES 寄存器设备驱动 SEJ 引擎：HACC 启动顺序、块运行循环寻址、未就绪中止、
/// 硬件码强制的非遗留启动，以及 seccfg 摘要解析与重写往返。</para>
/// </summary>
public sealed class MtkSejCryptoEngineTests
{
    private const uint SejBase = 0x1000A000;

    [Fact]
    public async Task RunAes128CbcAsync_RoundTripsThroughTheScriptedHaccDevice()
    {
        var device = new ScriptedSejDevice();
        var engine = new MtkSejCryptoEngine(0x8167, SejBase, device);
        var data = new byte[32];
        Random.Shared.NextBytes(data);

        var encrypted = await engine.RunAes128CbcAsync(data, encrypt: true, legacy: false,
            TestContext.Current.CancellationToken);
        var decrypted = await engine.RunAes128CbcAsync(encrypted, encrypt: false, legacy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, decrypted);
    }

    [Fact]
    public async Task RunSecCfgHardwareAsync_WrapsWithTheDocumentedConfigurationWords()
    {
        var device = new ScriptedSejDevice();
        var engine = new MtkSejCryptoEngine(0x8167, SejBase, device);
        var data = new byte[32];
        Random.Shared.NextBytes(data);

        var encrypted = await engine.RunSecCfgHardwareAsync(data, encrypt: true,
            TestContext.Current.CancellationToken);
        var decrypted = await engine.RunSecCfgHardwareAsync(encrypted, encrypt: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, decrypted);
        Assert.Contains(device.Ops, op => op is { Address: SejBase + 0x0040, IsWrite: true });
    }

    [Fact]
    public async Task RunAes128CbcAsync_NeverReadyDevice_AbortsWithProtocolError()
    {
        var device = new ScriptedSejDevice { ReadyAlwaysSet = false };
        var engine = new MtkSejCryptoEngine(0x8167, SejBase, device);

        await Assert.ThrowsAsync<MtkProtocolException>(() => engine.RunAes128CbcAsync(
            new byte[32], encrypt: true, legacy: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_ForcedNonLegacyChip_SkipsTheLegacySecInitSequence()
    {
        var forced = new ScriptedSejDevice();
        var legacy = new ScriptedSejDevice();
        await new MtkSejCryptoEngine(0x6795, SejBase, forced).RunAes128CbcAsync(
            new byte[32], encrypt: true, legacy: true, TestContext.Current.CancellationToken);
        await new MtkSejCryptoEngine(0x8167, SejBase, legacy).RunAes128CbcAsync(
            new byte[32], encrypt: true, legacy: true, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(0x40000002u, forced.Acon2Writes);
        Assert.Contains(0x40000002u, legacy.Acon2Writes);
    }

    [Fact]
    public async Task SecCfgHashRoundTrip_HardwareProtectedImage_ResolvesAndRewritesThroughTheEngine()
    {
        var device = new ScriptedSejDevice();
        var engine = new MtkSejCryptoEngine(0x8167, SejBase, device);
        var softwareImage = MtkSecCfg.CreateVersion4(BuildSoftwareReport(), MtkSecCfgLockIntent.Unlock);
        Assert.True(MtkSecCfg.TryReadVersion4(softwareImage, out var softwareReport) && softwareReport is not null);
        var header = Convert.FromHexString(softwareReport.HeaderHex);

        var stored = await engine.TransformAsync(MtkSecCfgHashKind.HardwareV3,
            SHA256.HashData(header), encrypt: true, TestContext.Current.CancellationToken);
        var hardwareImage = BuildImageWithHash(header, stored);

        var resolved = await MtkSecCfg.TryReadVersion4Async(hardwareImage, engine,
            TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal(MtkSecCfgHashKind.HardwareV3, resolved.HashKind);

        var rewritten = await MtkSecCfg.CreateVersion4Async(resolved, MtkSecCfgLockIntent.Lock, engine,
            TestContext.Current.CancellationToken);
        var reread = await MtkSecCfg.TryReadVersion4Async(rewritten, engine, TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal(MtkSecCfgHashKind.HardwareV3, reread.HashKind);
        Assert.Equal(1u, reread.LockStateRaw);
    }

    private static MtkSecCfgV4Report BuildSoftwareReport()
    {
        var header = new byte[MtkSecCfg.Version4HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), MtkSecCfg.Version4AgreedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), MtkSecCfg.EndFlag);
        using var aes = Aes.Create();
        aes.Key = "25A1763A21BC854CD569DC23B4782B63"u8.ToArray();
        aes.IV = Convert.FromHexString("57325A5A125497661254976657325A5A");
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        var stored = aes.CreateEncryptor().TransformFinalBlock(SHA256.HashData(header), 0, 32);
        var image = BuildImageWithHash(header, stored);
        Assert.True(MtkSecCfg.TryReadVersion4(image, out var report));
        return report!;
    }

    private static byte[] BuildImageWithHash(byte[] header, byte[] hash)
    {
        var image = new byte[MtkSecCfg.ImageAlignment];
        header.CopyTo(image, 0);
        hash.CopyTo(image.AsSpan(header.Length));
        return image;
    }

    /// <summary>
    /// A scripted HACC device: AES-128-CBC with a fixed all-zero key over the programmed ACFG vector,
    /// completing every START on the next register read.
    /// <para>脚本化 HACC 设备：以固定全零密钥与编程的 ACFG 向量做 AES-128-CBC，每次 START 在下一次
    /// 寄存器读取时完成。</para></summary>
    private sealed class ScriptedSejDevice : IMtkSejRegisterAccess
    {
        private readonly Dictionary<uint, uint> _registers = [];
        private readonly Aes _aes = Aes.Create();

        public List<(uint Address, bool IsWrite)> Ops { get; } = [];

        public List<uint> Acon2Writes { get; } = [];

        public bool ReadyAlwaysSet { get; init; } = true;

        public ScriptedSejDevice()
        {
            _aes.Key = new byte[16];
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.None;
        }

        public async Task<uint> ReadWordAsync(uint address, CancellationToken cancellationToken)
        {
            Ops.Add((address, IsWrite: false));
            _registers.TryGetValue(address, out var value);
            if (address == SejBase + 0x0008)
            {
                if ((value & 0x40000000) != 0 && ReadyAlwaysSet)
                {
                    value = (value & 0xBFFFFFFF) | 0x80000000;
                }
                if (ReadyAlwaysSet)
                {
                    value |= 0x8000;
                }
            }
            await Task.Yield();
            return value;
        }

        public async Task WriteWordAsync(uint address, uint value, CancellationToken cancellationToken)
        {
            Ops.Add((address, IsWrite: true));
            if (address == SejBase + 0x0008)
            {
                Acon2Writes.Add(value);
            }
            _registers[address] = value;
            if (address == SejBase + 0x0008 && (value & 0x1) != 0 && ReadyAlwaysSet)
            {
                RunBlock();
                _registers[address] = value | 0x8000;
            }
            await Task.Yield();
        }

        private void RunBlock()
        {
            var source = ReadWords(0x0010, 4);
            var iv = ReadWords(0x0040, 4);
            var ivBytes = new byte[16];
            for (var index = 0; index < 4; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ivBytes.AsSpan(index * 4), iv[index]);
            }
            _aes.IV = ivBytes;
            _registers.TryGetValue(SejBase + 0x0004, out var acon);
            var transform = (acon & 0x1) != 0 ? _aes.CreateEncryptor() : _aes.CreateDecryptor();
            var output = transform.TransformFinalBlock(source, 0, source.Length);
            for (var index = 0; index < 4; index++)
            {
                _registers[SejBase + 0x0050 + (uint)(index * 4)] =
                    BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(index * 4));
            }
        }

        private byte[] ReadWords(uint offset, int count)
        {
            var bytes = new byte[count * 4];
            for (var index = 0; index < count; index++)
            {
                _registers.TryGetValue(SejBase + offset + (uint)(index * 4), out var value);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), value);
            }
            return bytes;
        }
    }
}
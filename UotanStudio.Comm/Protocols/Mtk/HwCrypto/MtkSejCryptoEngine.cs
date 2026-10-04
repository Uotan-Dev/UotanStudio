using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// The register seam the SEJ engine drives: one word per read or write, addressed absolutely.
/// <para>SEJ 引擎驱动的寄存器接缝：每次读写一个字，使用绝对地址。</para></summary>
internal interface IMtkSejRegisterAccess
{
    /// <summary>Reads one register word. <para>读取一个寄存器字。</para></summary>
    /// <param name="address">The absolute register address. <para>绝对寄存器地址。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <returns>The register value. <para>寄存器数值。</para></returns>
    Task<uint> ReadWordAsync(uint address, CancellationToken cancellationToken);

    /// <summary>Writes one register word. <para>写入一个寄存器字。</para></summary>
    /// <param name="address">The absolute register address. <para>绝对寄存器地址。</para></param>
    /// <param name="value">The word to store. <para>要写入的字。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    Task WriteWordAsync(uint address, uint value, CancellationToken cancellationToken);
}

/// <summary>
/// Adapter binding the SEJ engine to the download-agent register primitives the other hardware-crypto
/// engines use, matching the upstream <c>read32</c>/<c>write32</c> wiring of the Boot ROM context.
/// <para>把 SEJ 引擎绑定到其他硬件加密引擎所用的下载代理寄存器原语上的适配器，与上游 Boot ROM 场景的
/// <c>read32</c>/<c>write32</c> 接线一致。</para></summary>
internal sealed class MtkDaSejRegisterAccess(MtkDaRegisterAccess registers) : IMtkSejRegisterAccess
{
    /// <inheritdoc/>
    public async Task<uint> ReadWordAsync(uint address, CancellationToken cancellationToken) =>
        (await registers.ReadRegisterWordsAsync(address, 1, cancellationToken).ConfigureAwait(false))[0];

    /// <inheritdoc/>
    public Task WriteWordAsync(uint address, uint value, CancellationToken cancellationToken) =>
        registers.WriteRegisterWordsAsync(address, [value], cancellationToken);
}

/// <summary>
/// The MediaTek SEJ (Security Engine J) HACC engine: the register bring-up, the 16-byte block run loop and
/// the secure-boot-configuration hash variants the reference client drives against a running target.
/// <para>联发科 SEJ（Security Engine J）HACC 引擎：寄存器启动序列、16 字节块运行循环，以及参考客户端在
/// 运行中的目标上驱动的安全启动配置摘要变体。</para></summary>
/// <remarks>
/// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_sej.py</c>: the HACC register offsets come from
/// lines 106-133, the flag words from lines 143-172, the fixed configuration words from lines 176-226,
/// <c>SEJ_V3_Init</c> from lines 800-884, <c>HACC_V3_Run</c> from lines 505-548 (as invoked for the
/// configuration paths), <c>sej_terminate</c> from lines 789-798, <c>xor_data</c> from lines 963-976,
/// <c>sej_sec_cfg_hw</c> from lines 982-994 and <c>hw_aes128_cbc_encrypt</c> from lines 884-893. One documented
/// deviation: where the reference logs "SEJ Hardware seems not to be configured correctly" after twenty polls
/// and continues (lines 541-546 and 874-879), this engine aborts, because the caller would otherwise compare
/// or persist a digest the hardware never produced.</para>
/// <para>转录自 <c>mtkclient/Library/Hardware/hwcrypto_sej.py</c>：HACC 寄存器偏移来自第 106-133 行，标志字来自
/// 第 143-172 行，固定配置字来自第 176-226 行，<c>SEJ_V3_Init</c> 来自第 800-884 行，<c>HACC_V3_Run</c>（按配置
/// 路径的实际调用形态）来自第 505-548 行，<c>sej_terminate</c> 来自第 789-798 行，<c>xor_data</c> 来自第 963-976
/// 行，<c>sej_sec_cfg_hw</c> 来自第 982-994 行，<c>hw_aes128_cbc_encrypt</c> 来自第 884-893 行。唯一记录在案的偏差：
/// 参考实现轮询二十次后记录 "SEJ Hardware seems not to be configured correctly" 并继续（第 541-546 与 874-879 行），
/// 本引擎则中止，否则调用方会比对或持久化一个硬件从未产生过的摘要。</para>
/// </remarks>
/// <param name="hardwareCode">The hardware code reported by the boot handshake; it selects the chips the
/// reference forces onto the non-legacy bring-up.
/// <para>启动握手上报的硬件码；它决定参考实现对哪些芯片强制使用非遗留启动序列。</para></param>
/// <param name="sejBase">The SEJ peripheral base recorded for the chip.
/// <para>该芯片记录的 SEJ 外设基址。</para></param>
/// <param name="registers">The register seam. <para>寄存器接缝。</para></param>
internal sealed class MtkSejCryptoEngine(ushort hardwareCode, uint sejBase, IMtkSejRegisterAccess registers)
    : IMtkSecCfgHashEngine
{
    /// <summary>The AES block size the HACC run loop processes per iteration.
    /// <para>HACC 运行循环每次迭代处理的 AES 块大小。</para></summary>
    internal const int BlockBytes = 16;

    /// <summary>Applies the secure-boot-configuration hash transform of a resolved hardware kind.
    /// <para>应用已解析硬件种类的安全启动配置摘要变换。</para></summary>
    /// <param name="kind">The resolved hardware hash kind. <para>已解析的硬件摘要种类。</para></param>
    /// <param name="hash">The block-aligned digest bytes. <para>按块对齐的摘要字节。</para></param>
    /// <param name="encrypt">Whether the forward transform runs. <para>是否运行正向变换。</para></param>
    /// <param name="cancellationToken">Cancellation for every register exchange.
    /// <para>每次寄存器交换的取消令牌。</para></param>
    /// <returns>The transformed bytes. <para>变换后的字节。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is a host-reproducible or unresolved kind.
    /// <para><paramref name="kind"/> 为主机可复现或未解析种类。</para></exception>
    public async Task<byte[]> TransformAsync(MtkSecCfgHashKind kind, ReadOnlyMemory<byte> hash, bool encrypt,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            MtkSecCfgHashKind.HardwareV3 => await RunAes128CbcAsync(hash, encrypt, legacy: false, cancellationToken)
                .ConfigureAwait(false),
            MtkSecCfgHashKind.HardwareV3Legacy => await RunAes128CbcAsync(hash, encrypt, legacy: true,
                cancellationToken).ConfigureAwait(false),
            MtkSecCfgHashKind.Hardware => await RunSecCfgHardwareAsync(hash, encrypt, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"The hash kind {kind} is not a target-driven hardware transform.", nameof(kind))
        };
    }

    private const uint HaccAcon = 0x0004;
    private const uint HaccAcon2 = 0x0008;
    private const uint HaccAconK = 0x000C;
    private const uint HaccAsrc0 = 0x0010;
    private const uint HaccAkey0 = 0x0020;
    private const uint HaccAcfg0 = 0x0040;
    private const uint HaccAout0 = 0x0050;
    private const uint HaccSecInit0New = 0x00BC;

    private const uint HaccAesDec = 0x00000000;
    private const uint HaccAesEnc = 0x00000001;
    private const uint HaccAesCbc = 0x00000002;
    private const uint HaccAes128 = 0x00000000;
    private const uint HaccAesChgBoOff = 0x00000000;
    private const uint HaccAesBk2C = 0x00000010;
    private const uint HaccAesR2K = 0x00000100;
    private const uint HaccAesStart = 0x00000001;
    private const uint HaccAesClr = 0x00000002;
    private const uint HaccAesRdy = 0x00008000;

    // g_CFG_RANDOM_PATTERN (hwcrypto_sej.py, lines 183-196).
    private static readonly uint[] GCfgRandomPattern =
    [
        0x2D44BB70, 0xA744D227, 0xD0A9864B, 0x83FFC244,
        0x7EC8266B, 0x43E80FB2, 0x01A6348A, 0x2067F9A0,
        0x54536405, 0xD546A6B1, 0x1CC3EC3A, 0xDE377A83
    ];

    // g_HACC_CFG_1 (hwcrypto_sej.py, lines 198-200).
    private static readonly uint[] GHaccCfg1 =
    [
        0x9ED40400, 0x00E884A1, 0xE3F083BD, 0x2F4E6D8A,
        0xFF838E5C, 0xE940A0E3, 0x8D4DECC6, 0x45FC0989
    ];

    // hwcode values SEJ_V3_Init forces onto the non-legacy bring-up (hwcrypto_sej.py, lines 802-806).
    private static readonly ushort[] NonLegacyForcedHardwareCodes = [0x6795, 0x335, 0x321, 0x337, 0x279, 0x1375];

    private readonly bool _legacyForcedOff = Array.IndexOf(NonLegacyForcedHardwareCodes, hardwareCode) >= 0;

    /// <summary>
    /// Runs the secure-boot-configuration hardware variant with the legacy bring-up
    /// (<c>sej_sec_cfg_hw</c>): XOR wrapping, the legacy bring-up, the block run and termination.
    /// <para>以遗留启动序列运行安全启动配置硬件变体（<c>sej_sec_cfg_hw</c>）：XOR 包裹、遗留启动、块运行与终止。</para>
    /// </summary>
    /// <param name="data">The input, a positive multiple of <see cref="BlockBytes"/> within the transfer bounds.
    /// <para>输入，为 <see cref="BlockBytes"/> 正整数倍且不超出传输上限。</para></param>
    /// <param name="encrypt">Whether the engine runs the encryption direction.
    /// <para>引擎是否运行加密方向。</para></param>
    /// <param name="cancellationToken">Cancellation for every register exchange.
    /// <para>每次寄存器交换的取消令牌。</para></param>
    /// <returns>The transformed bytes. <para>变换后的字节。</para></returns>
    internal async Task<byte[]> RunSecCfgHardwareAsync(ReadOnlyMemory<byte> data, bool encrypt,
        CancellationToken cancellationToken)
    {
        var buffer = ValidateAndCopy(data);
        if (encrypt)
        {
            XorData(buffer);
        }
        await InitializeAsync(ben: encrypt, GHaccCfg1, legacy: true, cancellationToken).ConfigureAwait(false);
        var output = await RunAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TerminateAsync(cancellationToken).ConfigureAwait(false);
        if (!encrypt)
        {
            XorData(output);
        }
        return output;
    }

    /// <summary>
    /// Runs the version 3 hardware variant (<c>hw_aes128_cbc_encrypt</c> / <c>sej_sec_cfg_hw_V3</c>):
    /// the fixed <c>g_HACC_CFG_1</c> vector with either bring-up, no XOR wrapping.
    /// <para>运行版本 3 硬件变体（<c>hw_aes128_cbc_encrypt</c> / <c>sej_sec_cfg_hw_V3</c>）：
    /// 固定 <c>g_HACC_CFG_1</c> 向量加任一启动序列，不做 XOR 包裹。</para>
    /// </summary>
    /// <param name="data">The input, a positive multiple of <see cref="BlockBytes"/> within the transfer bounds.
    /// <para>输入，为 <see cref="BlockBytes"/> 正整数倍且不超出传输上限。</para></param>
    /// <param name="encrypt">Whether the engine runs the encryption direction.
    /// <para>引擎是否运行加密方向。</para></param>
    /// <param name="legacy">Whether the legacy bring-up runs. <para>是否运行遗留启动序列。</para></param>
    /// <param name="cancellationToken">Cancellation for every register exchange.
    /// <para>每次寄存器交换的取消令牌。</para></param>
    /// <returns>The transformed bytes. <para>变换后的字节。</para></returns>
    internal async Task<byte[]> RunAes128CbcAsync(ReadOnlyMemory<byte> data, bool encrypt, bool legacy,
        CancellationToken cancellationToken)
    {
        var buffer = ValidateAndCopy(data);
        await InitializeAsync(ben: encrypt, GHaccCfg1, legacy, cancellationToken).ConfigureAwait(false);
        var output = await RunAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TerminateAsync(cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static byte[] ValidateAndCopy(ReadOnlyMemory<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(data.Length, BlockBytes);
        MtkDaRegisterAccess.ThrowUnlessTransferInBounds(data.Length);
        ArgumentOutOfRangeException.ThrowIfNotEqual(data.Length % BlockBytes, 0, nameof(data));
        return data.ToArray();
    }

    private async Task<uint> ReadAsync(uint offset, CancellationToken cancellationToken) =>
        await registers.ReadWordAsync(sejBase + offset, cancellationToken).ConfigureAwait(false);

    private Task WriteAsync(uint offset, uint value, CancellationToken cancellationToken) =>
        registers.WriteWordAsync(sejBase + offset, value, cancellationToken);

    /// <summary>Runs the SEJ_V3_Init bring-up sequence. <para>运行 SEJ_V3_Init 启动序列。</para></summary>
    /// <remarks>
    /// Transcribed from <c>hwcrypto_sej.py</c>, lines 800-884; the reference configuration paths never
    /// pass the no-read flag, so this transcription always reads the AOUT words.
    /// <para>转录自 <c>hwcrypto_sej.py</c> 第 800-884 行；参考配置路径从不传入 no-read 标志，因此本转录
    /// 总是读取 AOUT 字。</para>
    /// </remarks>
    private async Task InitializeAsync(bool ben, uint[] iv, bool legacy, CancellationToken cancellationToken)
    {
        if (_legacyForcedOff)
        {
            legacy = false;
        }
        var aconSetting = HaccAesChgBoOff | HaccAes128;
        aconSetting |= HaccAesCbc;
        aconSetting |= ben ? HaccAesEnc : HaccAesDec;

        for (var slot = 0; slot < 8; slot++)
        {
            await WriteAsync(HaccAkey0 + (uint)(slot * 4), 0, cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(HaccAcon, HaccAesChgBoOff | HaccAesCbc | HaccAesDec, cancellationToken).ConfigureAwait(false);
        await WriteAsync(HaccAconK, HaccAesBk2C, cancellationToken).ConfigureAwait(false);
        await WriteAsync(HaccAconK, HaccAesBk2C | HaccAesR2K, cancellationToken).ConfigureAwait(false);
        await WriteAsync(HaccAcon2, HaccAesClr, cancellationToken).ConfigureAwait(false);

        for (var slot = 0; slot < 4; slot++)
        {
            await WriteAsync(HaccAcfg0 + (uint)(slot * 4), iv[slot], cancellationToken).ConfigureAwait(false);
        }

        if (legacy)
        {
            await WriteAsync(HaccSecInit0New, await ReadAsync(HaccSecInit0New, cancellationToken).ConfigureAwait(false) | 2,
                cancellationToken).ConfigureAwait(false);
            await WriteAsync(HaccAcon2, HaccAesClr | 0x40000000, cancellationToken).ConfigureAwait(false);
            var polls = 0;
            while (polls < 20)
            {
                if (await ReadAsync(HaccAcon2, cancellationToken).ConfigureAwait(false) > 0x80000000)
                {
                    break;
                }
                polls++;
            }
            if (polls == 20)
            {
                throw new MtkProtocolException(
                    "The SEJ legacy hardware did not acknowledge the bring-up; the result would be unreliable.");
            }
            await WriteAsync(HaccSecInit0New,
                await ReadAsync(HaccSecInit0New, cancellationToken).ConfigureAwait(false) & 0xFFFFFFFE,
                cancellationToken).ConfigureAwait(false);
            await WriteAsync(HaccAconK, HaccAesBk2C, cancellationToken).ConfigureAwait(false);
            await WriteAsync(HaccAcon, aconSetting, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(HaccSecInit0New, 1, cancellationToken).ConfigureAwait(false);
            for (var round = 0; round < 3; round++)
            {
                var pos = round * 4;
                for (var slot = 0; slot < 4; slot++)
                {
                    await WriteAsync(HaccAsrc0 + (uint)(slot * 4), GCfgRandomPattern[pos + slot],
                        cancellationToken).ConfigureAwait(false);
                }
                await WriteAsync(HaccAcon2, HaccAesStart, cancellationToken).ConfigureAwait(false);
                var polls = 0;
                while (polls < 20)
                {
                    if ((await ReadAsync(HaccAcon2, cancellationToken).ConfigureAwait(false) & HaccAesRdy) != 0)
                    {
                        break;
                    }
                    polls++;
                }
                if (polls == 20)
                {
                    throw new MtkProtocolException(
                        "The SEJ hardware did not reach the ready flag during the HUID/HUK pattern rounds; the result would be unreliable.");
                }
            }
            await WriteAsync(HaccAcon2, HaccAesClr, cancellationToken).ConfigureAwait(false);
            for (var slot = 0; slot < 4; slot++)
            {
                await WriteAsync(HaccAcfg0 + (uint)(slot * 4), iv[slot], cancellationToken).ConfigureAwait(false);
            }
            await WriteAsync(HaccAcon, aconSetting, cancellationToken).ConfigureAwait(false);
            await WriteAsync(HaccAconK, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the HACC block loop. <para>运行 HACC 块循环。</para></summary>
    /// <remarks>
    /// Transcribed from <c>hwcrypto_sej.py</c>, lines 505-548 as the configuration paths invoke it: the
    /// reference callers pass <c>noread=false</c>, <c>legacy=false</c>, <c>attr=0</c> and
    /// <c>sej_param=0</c>, so the loop always reads AOUT and never enters the key-feedback branch.
    /// <para>按配置路径的实际调用形态转录自 <c>hwcrypto_sej.py</c> 第 505-548 行：参考调用方传入
    /// <c>noread=false</c>、<c>legacy=false</c>、<c>attr=0</c> 与 <c>sej_param=0</c>，因此循环总是读取 AOUT
    /// 且不会进入密钥反馈分支。</para>
    /// </remarks>
    private async Task<byte[]> RunAsync(byte[] data, CancellationToken cancellationToken)
    {
        var output = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += BlockBytes)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                await WriteAsync(HaccAsrc0 + (uint)(slot * 4),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + slot * 4)),
                    cancellationToken).ConfigureAwait(false);
            }
            await WriteAsync(HaccAcon2, HaccAesStart, cancellationToken).ConfigureAwait(false);
            var polls = 0;
            while (polls < 20)
            {
                if ((await ReadAsync(HaccAcon2, cancellationToken).ConfigureAwait(false) & HaccAesRdy) != 0)
                {
                    break;
                }
                polls++;
            }
            if (polls == 20)
            {
                throw new MtkProtocolException(
                    "The SEJ hardware did not reach the ready flag for a data block; the result would be unreliable.");
            }
            for (var slot = 0; slot < 4; slot++)
            {
                var word = await ReadAsync(HaccAout0 + (uint)(slot * 4), cancellationToken).ConfigureAwait(false);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + slot * 4), word);
            }
        }
        return output;
    }

    /// <summary>XORs the first 16 bytes with <c>g_HACC_CFG_1</c> (<c>hwcrypto_sej.py</c>, lines 963-976).
    /// <para>把前 16 字节与 <c>g_HACC_CFG_1</c> 异或（<c>hwcrypto_sej.py</c> 第 963-976 行）。</para></summary>
    private static void XorData(byte[] data)
    {
        for (var index = 0; index < 4; index++)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 4), word ^ GHaccCfg1[index]);
        }
    }

    /// <summary>Clears the run flag and the key registers (<c>sej_terminate</c>,
    /// <c>hwcrypto_sej.py</c>, lines 789-798).
    /// <para>清除运行标志与密钥寄存器（<c>sej_terminate</c>，<c>hwcrypto_sej.py</c> 第 789-798 行）。</para></summary>
    private async Task TerminateAsync(CancellationToken cancellationToken)
    {
        await WriteAsync(HaccAcon2, HaccAesClr, cancellationToken).ConfigureAwait(false);
        for (var slot = 0; slot < 8; slot++)
        {
            await WriteAsync(HaccAkey0 + (uint)(slot * 4), 0, cancellationToken).ConfigureAwait(false);
        }
    }
}
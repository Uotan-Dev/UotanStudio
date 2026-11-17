using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Byte layout of one of the three RCM message headers. The layout fixes the header size and the offset of every
/// field, so no caller needs to hard-code a magic number.
/// <para>三种 RCM 消息头之一的字节布局。布局固定了头部大小与每个字段的偏移，调用方无需硬编码魔数。</para>
/// </summary>
/// <remarks>
/// The three layouts differ in whether a 256-byte RSA modulus and an object signature precede the signed region,
/// and in whether the object signature reserves the CMAC hash alone (rcm1) or the CMAC hash followed by an RSA-PSS
/// signature (rcm35, rcm40). Offsets follow the byte-range comments of <c>tegrarcm</c>'s <c>src/rcm.h</c> exactly.
/// <para>三种布局的差异在于签名区之前是否有 256 字节 RSA 模数与对象签名，以及对象签名只预留 CMAC 摘要（rcm1）
/// 还是同时预留 CMAC 摘要与随后的 RSA-PSS 签名（rcm35、rcm40）。偏移严格对应 <c>tegrarcm</c> 的
/// <c>src/rcm.h</c> 中逐字段的字节区间注释。</para>
/// </remarks>
public sealed class TegraRcmHeaderLayout
{
    private TegraRcmHeaderLayout(
        TegraChip chip,
        uint rcmVersion,
        int headerLength,
        int modulusOffset,
        int modulusLength,
        int cmacOffset,
        int rsaSignatureOffset,
        int ecidOffset,
        int signedDataOffset,
        int opcodeOffset,
        int lengthSecureOffset,
        int payloadLengthOffset,
        int rcmVersionOffset,
        int argsOffset,
        int paddingOffset)
    {
        Chip = chip;
        RcmVersion = rcmVersion;
        HeaderLength = headerLength;
        ModulusOffset = modulusOffset;
        ModulusLength = modulusLength;
        CmacOffset = cmacOffset;
        RsaSignatureOffset = rsaSignatureOffset;
        EcidOffset = ecidOffset;
        SignedDataOffset = signedDataOffset;
        OpcodeOffset = opcodeOffset;
        LengthSecureOffset = lengthSecureOffset;
        PayloadLengthOffset = payloadLengthOffset;
        RcmVersionOffset = rcmVersionOffset;
        ArgsOffset = argsOffset;
        PaddingOffset = paddingOffset;
    }

    /// <summary>
    /// Gets the rcm1 layout: 0x74 bytes with a 16-byte CMAC hash and no modulus, ECID or RSA signature field
    /// (Tegra 2 and Tegra 3).
    /// <para>获取 rcm1 布局：共 0x74 字节，只有 16 字节 CMAC 摘要，没有模数、ECID 与 RSA 签名字段（Tegra 2 与
    /// Tegra 3）。</para>
    /// </summary>
    public static TegraRcmHeaderLayout Rcm1 { get; } = new(
        TegraChip.Tegra20,
        TegraConstants.RcmVersion1,
        headerLength: 0x74,
        modulusOffset: -1,
        modulusLength: 0,
        cmacOffset: 0x04,
        rsaSignatureOffset: -1,
        ecidOffset: -1,
        signedDataOffset: 0x14,
        opcodeOffset: 0x24,
        lengthSecureOffset: 0x28,
        payloadLengthOffset: 0x2C,
        rcmVersionOffset: 0x30,
        argsOffset: 0x34,
        paddingOffset: 0x64);

    /// <summary>
    /// Gets the rcm35 layout: 0x274 bytes with a 256-byte modulus, a union that holds the 16-byte CMAC hash or the
    /// 256-byte RSA-PSS signature, and a four-word ECID (Tegra 4).
    /// <para>获取 rcm35 布局：共 0x274 字节，含 256 字节模数、共用一个区域的 16 字节 CMAC 摘要或 256 字节
    /// RSA-PSS 签名，以及四个字的 ECID（Tegra 4）。</para>
    /// </summary>
    public static TegraRcmHeaderLayout Rcm35 { get; } = new(
        TegraChip.Tegra114,
        TegraConstants.RcmVersion35,
        headerLength: 0x274,
        modulusOffset: 0x04,
        modulusLength: TegraConstants.RsaModulusSize,
        cmacOffset: 0x104,
        rsaSignatureOffset: 0x104,
        ecidOffset: 0x214,
        signedDataOffset: 0x204,
        opcodeOffset: 0x224,
        lengthSecureOffset: 0x228,
        payloadLengthOffset: 0x22C,
        rcmVersionOffset: 0x230,
        argsOffset: 0x234,
        paddingOffset: 0x264);

    /// <summary>
    /// Gets the rcm40 layout: 0x284 bytes with a 256-byte modulus, a separate 16-byte CMAC hash and 256-byte
    /// RSA-PSS signature, and a four-word ECID (Tegra K1).
    /// <para>获取 rcm40 布局：共 0x284 字节，含 256 字节模数、各自独立的 16 字节 CMAC 摘要与 256 字节 RSA-PSS
    /// 签名，以及四个字的 ECID（Tegra K1）。</para>
    /// </summary>
    public static TegraRcmHeaderLayout Rcm40 { get; } = new(
        TegraChip.Tegra124,
        TegraConstants.RcmVersion40,
        headerLength: 0x284,
        modulusOffset: 0x04,
        modulusLength: TegraConstants.RsaModulusSize,
        cmacOffset: 0x104,
        rsaSignatureOffset: 0x114,
        ecidOffset: 0x224,
        signedDataOffset: 0x214,
        opcodeOffset: 0x234,
        lengthSecureOffset: 0x238,
        payloadLengthOffset: 0x23C,
        rcmVersionOffset: 0x240,
        argsOffset: 0x244,
        paddingOffset: 0x274);

    /// <summary>
    /// Gets the chip generation this layout is registered for. <see cref="TegraChip.Tegra20"/> and
    /// <see cref="TegraChip.Tegra30"/> share <see cref="Rcm1"/>, which reports <see cref="TegraChip.Tegra20"/>.
    /// <para>获取本布局登记的芯片代际。<see cref="TegraChip.Tegra20"/> 与 <see cref="TegraChip.Tegra30"/> 共用
    /// <see cref="Rcm1"/>，该布局回报 <see cref="TegraChip.Tegra20"/>。</para>
    /// </summary>
    public TegraChip Chip { get; }

    /// <summary>
    /// Gets the packed <c>RCM_VERSION_*</c> word written into the header's <c>rcm_version</c> field.
    /// <para>获取写入头部 <c>rcm_version</c> 字段的打包 <c>RCM_VERSION_*</c> 字。</para>
    /// </summary>
    public uint RcmVersion { get; }

    /// <summary>
    /// Gets the header length in bytes: 0x74 for <see cref="Rcm1"/>, 0x274 for <see cref="Rcm35"/> and 0x284 for
    /// <see cref="Rcm40"/>.
    /// <para>获取头部字节长度：<see cref="Rcm1"/> 为 0x74，<see cref="Rcm35"/> 为 0x274，<see cref="Rcm40"/> 为
    /// 0x284。</para>
    /// </summary>
    public int HeaderLength { get; }

    /// <summary>
    /// Gets the offset of the 256-byte RSA modulus, or <c>-1</c> when the layout has none.
    /// <para>获取 256 字节 RSA 模数的偏移；布局不含该字段时为 <c>-1</c>。</para>
    /// </summary>
    public int ModulusOffset { get; }

    /// <summary>
    /// Gets the length of the RSA modulus field: 256 for rcm35 and rcm40, 0 for rcm1.
    /// <para>获取 RSA 模数字段长度：rcm35 与 rcm40 为 256，rcm1 为 0。</para>
    /// </summary>
    public int ModulusLength { get; }

    /// <summary>
    /// Gets the offset of the 16-byte AES-CMAC hash.
    /// <para>获取 16 字节 AES-CMAC 摘要的偏移。</para>
    /// </summary>
    public int CmacOffset { get; }

    /// <summary>
    /// Gets the offset of the 256-byte RSA-PSS signature, or <c>-1</c> when the layout has no signature field. On
    /// rcm35 it equals <see cref="CmacOffset"/> because both occupy one union; on rcm1 it shares
    /// <see cref="CmacOffset"/> too, but the union holds only the 16-byte hash there.
    /// <para>获取 256 字节 RSA-PSS 签名的偏移；布局无签名字段时为 <c>-1</c>。rcm35 上它与
    /// <see cref="CmacOffset"/> 相同，因为两者共用同一区域；rcm1 上该区域也只有 16 字节摘要。
    /// </para>
    /// </summary>
    public int RsaSignatureOffset { get; }

    /// <summary>
    /// Gets the offset of the four-word ECID array, or <c>-1</c> when the layout has no ECID field.
    /// <para>获取四字 ECID 数组的偏移；布局不含 ECID 字段时为 <c>-1</c>。</para>
    /// </summary>
    public int EcidOffset { get; }

    /// <summary>
    /// Gets the offset where the signed region begins: 0x14 for rcm1, 0x204 for rcm35 and 0x214 for rcm40.
    /// Everything from here to the end of the message is covered by the CMAC and the RSA-PSS signature.
    /// <para>获取签名区的起始偏移：rcm1 为 0x14，rcm35 为 0x204，rcm40 为 0x214。自此到消息末尾的全部字节都受
    /// CMAC 与 RSA-PSS 签名覆盖。</para>
    /// </summary>
    public int SignedDataOffset { get; }

    /// <summary>
    /// Gets the offset of the 32-bit <c>opcode</c> field.
    /// <para>获取 32 位 <c>opcode</c> 字段的偏移。</para>
    /// </summary>
    public int OpcodeOffset { get; }

    /// <summary>
    /// Gets the offset of the 32-bit <c>len_secure</c> field.
    /// <para>获取 32 位 <c>len_secure</c> 字段的偏移。</para>
    /// </summary>
    public int LengthSecureOffset { get; }

    /// <summary>
    /// Gets the offset of the 32-bit <c>payload_len</c> field.
    /// <para>获取 32 位 <c>payload_len</c> 字段的偏移。</para>
    /// </summary>
    public int PayloadLengthOffset { get; }

    /// <summary>
    /// Gets the offset of the 32-bit <c>rcm_version</c> field.
    /// <para>获取 32 位 <c>rcm_version</c> 字段的偏移。</para>
    /// </summary>
    public int RcmVersionOffset { get; }

    /// <summary>
    /// Gets the offset of the 48-byte <c>args</c> field.
    /// <para>获取 48 字节 <c>args</c> 字段的偏移。</para>
    /// </summary>
    public int ArgsOffset { get; }

    /// <summary>
    /// Gets the offset of the trailing 16-byte <c>padding</c> field of the header.
    /// <para>获取头部末尾 16 字节 <c>padding</c> 字段的偏移。</para>
    /// </summary>
    public int PaddingOffset { get; }

    /// <summary>
    /// Gets whether the layout carries an RSA modulus (rcm35 and rcm40).
    /// <para>获取布局是否携带 RSA 模数（rcm35 与 rcm40）。</para>
    /// </summary>
    public bool HasRsaFields => ModulusLength > 0;

    /// <summary>
    /// Resolves the layout a chip generation uses.
    /// <para>解析某芯片代际使用的布局。</para>
    /// </summary>
    /// <param name="chip">The chip generation; <see cref="TegraChip.Tegra30"/> shares <see cref="Rcm1"/> with
    /// <see cref="TegraChip.Tegra20"/>. <para>芯片代际；<see cref="TegraChip.Tegra30"/> 与
    /// <see cref="TegraChip.Tegra20"/> 共用 <see cref="Rcm1"/>。</para></param>
    /// <returns>The matching layout. <para>匹配的布局。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is
    /// <see cref="TegraChip.Unknown"/>, which has no RCM layout.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>，该值没有 RCM 布局。</para></exception>
    public static TegraRcmHeaderLayout For(TegraChip chip) => chip switch
    {
        TegraChip.Tegra20 or TegraChip.Tegra30 => Rcm1,
        TegraChip.Tegra114 => Rcm35,
        TegraChip.Tegra124 => Rcm40,
        _ => throw new ArgumentOutOfRangeException(
            nameof(chip),
            chip,
            "An unknown Tegra chip has no RCM header layout; resolve the chip from the USB product ID first."),
    };

    /// <summary>
    /// Resolves the layout that matches a packed <c>RCM_VERSION_*</c> word.
    /// <para>解析与打包 <c>RCM_VERSION_*</c> 字匹配的布局。</para>
    /// </summary>
    /// <param name="rcmVersion">The packed version word, as read from a header or a device reply.
    /// <para>打包版本字，取自头部或设备应答。</para></param>
    /// <param name="layout">Receives the matching layout, or <see langword="null"/> when no layout matches.
    /// <para>接收匹配的布局；无匹配时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a layout matched. <para>匹配到布局时为 <see langword="true"/>。</para></returns>
    public static bool TryForVersion(uint rcmVersion, out TegraRcmHeaderLayout? layout)
    {
        layout = rcmVersion switch
        {
            TegraConstants.RcmVersion1 => Rcm1,
            TegraConstants.RcmVersion35 => Rcm35,
            TegraConstants.RcmVersion40 => Rcm40,
            _ => null,
        };

        return layout is not null;
    }

    /// <summary>
    /// Resolves the layout that matches a packed <c>RCM_VERSION_*</c> word.
    /// <para>解析与打包 <c>RCM_VERSION_*</c> 字匹配的布局。</para>
    /// </summary>
    /// <param name="rcmVersion">The packed version word, as read from a header or a device reply.
    /// <para>打包版本字，取自头部或设备应答。</para></param>
    /// <returns>The matching layout. <para>匹配的布局。</para></returns>
    /// <exception cref="InvalidDataException">No layout matches <paramref name="rcmVersion"/>, so the bytes cannot be
    /// a message this host knows how to read. <para>没有布局匹配 <paramref name="rcmVersion"/>，这些字节不可能
    /// 是本主机能读懂的消息。</para></exception>
    public static TegraRcmHeaderLayout ForVersion(uint rcmVersion)
    {
        if (!TryForVersion(rcmVersion, out var layout) || layout is null)
        {
            throw new InvalidDataException(
                $"RCM version 0x{rcmVersion:X8} matches no known header layout; expected 0x{TegraConstants.RcmVersion1:X8}, " +
                $"0x{TegraConstants.RcmVersion35:X8} or 0x{TegraConstants.RcmVersion40:X8}.");
        }

        return layout;
    }
}

/// <summary>
/// One unsigned RCM message: the header fields, the optional payload and the padding that follows it.
/// <para>一条未签名的 RCM 消息：头部字段、可选负载以及尾随填充。</para>
/// </summary>
/// <remarks>
/// This is a pure codec. It never touches USB and never signs: <see cref="Create"/> leaves the CMAC hash and the
/// RSA-PSS signature zeroed, and the signing layer fills them in place inside <see cref="SignedData"/> before the
/// frame goes on the wire. Device replies are not shaped like host messages, so they travel through
/// <see cref="TegraRcmReply"/> instead.
/// <para>本类型只是编解码器，不接触 USB，也不做任何签名：<see cref="Create"/> 把 CMAC 摘要与 RSA-PSS 签名留为
/// 零，签名层在上线前于 <see cref="SignedData"/> 中就地填入。设备应答与主机消息形态不同，经
/// <see cref="TegraRcmReply"/> 表示。</para>
/// </remarks>
public sealed class TegraRcmMessage
{
    /// <summary>
    /// Largest payload a single host message may carry (32 MiB). RCM payloads are miniloaders and BCT images, so
    /// the cap only exists to keep a crafted length from driving an allocation.
    /// <para>单条主机消息可承载的最大负载（32 MiB）。RCM 负载是 miniloader 与 BCT 镜像，该上限只用于阻止伪造
    /// 长度驱动一次分配。</para>
    /// </summary>
    public const int MaximumPayloadLength = 32 * 1024 * 1024;

    private byte[] _frame;

    private TegraRcmMessage(TegraRcmHeaderLayout layout, byte[] frame, int payloadLength)
    {
        Layout = layout;
        _frame = frame;
        PayloadLength = (uint)payloadLength;
    }

    /// <summary>
    /// Gets the header layout this message was built or parsed with.
    /// <para>获取构建或解析本消息所用的头部布局。</para>
    /// </summary>
    public TegraRcmHeaderLayout Layout { get; }

    /// <summary>
    /// Gets the total message length declared by <c>len_insecure</c>: header, payload and payload padding.
    /// <para>获取由 <c>len_insecure</c> 声明的消息总长度：头部、负载与负载填充。</para>
    /// </summary>
    public uint LengthInsecure => BinaryPrimitives.ReadUInt32LittleEndian(_frame);

    /// <summary>
    /// Gets the length declared by <c>len_secure</c>, which equals <see cref="LengthInsecure"/> for the unsigned
    /// messages this host sends.
    /// <para>获取 <c>len_secure</c> 声明的长度；对本主机发送的未签名消息而言等于 <see cref="LengthInsecure"/>。</para>
    /// </summary>
    public uint LengthSecure => BinaryPrimitives.ReadUInt32LittleEndian(_frame.AsSpan(Layout.LengthSecureOffset, 4));

    /// <summary>
    /// Gets the opcode carried in the header, including values outside <see cref="TegraRcmOpcode"/> that a device
    /// may report. <para>获取头部携带的操作码，包含设备可能上报的 <see cref="TegraRcmOpcode"/> 之外的取值。</para>
    /// </summary>
    public TegraRcmOpcode Opcode => (TegraRcmOpcode)BinaryPrimitives.ReadUInt32LittleEndian(_frame.AsSpan(Layout.OpcodeOffset, 4));

    /// <summary>
    /// Gets the payload length declared by the header's <c>payload_len</c> field.
    /// <para>获取头部 <c>payload_len</c> 字段声明的负载长度。</para>
    /// </summary>
    public uint PayloadLength { get; }

    /// <summary>
    /// Gets the packed <c>RCM_VERSION_*</c> word carried by the header.
    /// <para>获取头部携带的打包 <c>RCM_VERSION_*</c> 字。</para>
    /// </summary>
    public uint RcmVersion => BinaryPrimitives.ReadUInt32LittleEndian(_frame.AsSpan(Layout.RcmVersionOffset, 4));

    /// <summary>
    /// Gets the length of the payload-region padding: the bytes between the payload and the end of the message.
    /// <para>获取负载区填充长度：负载与消息末尾之间的字节数。</para>
    /// </summary>
    public int PayloadPaddingLength => checked((int)(LengthInsecure - (uint)Layout.HeaderLength - PayloadLength));

    /// <summary>
    /// Gets a copy of the payload bytes; empty when the message carries none.
    /// <para>获取负载字节的副本；消息不带负载时为空。</para>
    /// </summary>
    public byte[] Payload => _frame.AsSpan(Layout.HeaderLength, (int)PayloadLength).ToArray();

    /// <summary>
    /// Gets the payload bytes without copying, valid until the message is disposed.
    /// <para>不复制地获取负载字节，在消息释放前有效。</para>
    /// </summary>
    public ReadOnlyMemory<byte> PayloadMemory => _frame.AsMemory(Layout.HeaderLength, (int)PayloadLength);

    /// <summary>
    /// Gets a copy of the header bytes exactly as they are laid out on the wire.
    /// <para>获取头部字节的副本，与线上布局逐字节一致。</para>
    /// </summary>
    public byte[] Header => _frame.AsSpan(0, Layout.HeaderLength).ToArray();

    /// <summary>
    /// Gets a copy of the 48-byte <c>args</c> field.
    /// <para>获取 48 字节 <c>args</c> 字段的副本。</para>
    /// </summary>
    public byte[] Args => _frame.AsSpan(Layout.ArgsOffset, TegraConstants.ArgsLength).ToArray();

    /// <summary>
    /// Gets a copy of the 16-byte AES-CMAC hash, which is all zeros for a message that has not been signed.
    /// <para>获取 16 字节 AES-CMAC 摘要；未签名的消息全为零。</para>
    /// </summary>
    public byte[] CmacHash => _frame.AsSpan(Layout.CmacOffset, TegraConstants.AesBlockSize).ToArray();

    /// <summary>
    /// Gets a copy of the 256-byte RSA modulus, or <see langword="null"/> when the layout has no modulus field.
    /// <para>获取 256 字节 RSA 模数的副本；布局无模数字段时为 <see langword="null"/>。</para>
    /// </summary>
    public byte[]? Modulus => Layout.HasRsaFields
        ? _frame.AsSpan(Layout.ModulusOffset, Layout.ModulusLength).ToArray()
        : null;

    /// <summary>
    /// Gets a copy of the RSA-PSS signature, or <see langword="null"/> when the layout has no modulus field. On
    /// rcm35 the 256-byte signature shares its union with the CMAC hash.
    /// <para>获取 RSA-PSS 签名的副本；布局无模数字段时为 <see langword="null"/>。rcm35 上 256 字节签名与 CMAC
    /// 摘要共用同一区域。</para>
    /// </summary>
    public byte[]? RsaSignature => Layout.HasRsaFields
        ? _frame.AsSpan(Layout.RsaSignatureOffset, TegraConstants.RsaModulusSize).ToArray()
        : null;

    /// <summary>
    /// Gets the four-word ECID, or an empty array when the layout has no ECID field (rcm1).
    /// <para>获取四个字的 ECID；布局无 ECID 字段（rcm1）时为空数组。</para>
    /// </summary>
    public uint[] Ecid
    {
        get
        {
            if (Layout.EcidOffset < 0)
            {
                return [];
            }

            var words = new uint[4];
            for (var index = 0; index < words.Length; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    _frame.AsSpan(Layout.EcidOffset + (index * sizeof(uint)), sizeof(uint)));
            }

            return words;
        }
    }

    /// <summary>
    /// Gets the region the CMAC and RSA-PSS signature must cover: from <see cref="TegraRcmHeaderLayout.SignedDataOffset"/>
    /// to the end of the message, payload and padding included. A signer writes the hash and signature into the
    /// header in place, then calls <see cref="ToBytes"/>.
    /// <para>获取 CMAC 与 RSA-PSS 签名必须覆盖的区域：自 <see cref="TegraRcmHeaderLayout.SignedDataOffset"/> 起到
    /// 消息末尾，含负载与填充。签名方在头部就地写入摘要与签名，随后调用 <see cref="ToBytes"/>。</para>
    /// </summary>
    public ReadOnlySpan<byte> SignedData => _frame.AsSpan(Layout.SignedDataOffset);

    /// <summary>
    /// Builds an unsigned host message: header fields are filled in, <c>args</c> is zero-padded, the payload is
    /// copied, and both padding runs are filled with <see cref="TegraConstants.PaddingFillByte"/> followed by zeros.
    /// <para>构建未签名的主机消息：填入头部字段、把 <c>args</c> 补零、复制负载，并以
    /// <see cref="TegraConstants.PaddingFillByte"/> 加零填满头部与负载两段填充。</para>
    /// </summary>
    /// <param name="chip">The target chip generation, which selects the header layout.
    /// <para>目标芯片代际，用于选择头部布局。</para></param>
    /// <param name="opcode">The recovery mode command. <para>恢复模式命令。</para></param>
    /// <param name="args">The command arguments, at most 48 bytes; the rest of the field stays zero.
    /// <para>命令参数，最多 48 字节；字段其余部分保持为零。</para></param>
    /// <param name="payload">The payload, at most <see cref="MaximumPayloadLength"/> bytes; empty for a message
    /// without a payload. <para>负载，最多 <see cref="MaximumPayloadLength"/> 字节；无负载消息传空。</para></param>
    /// <returns>The serialisable message, with the CMAC hash and RSA signature left zeroed.
    /// <para>可序列化的消息，CMAC 摘要与 RSA 签名留为零。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is
    /// <see cref="TegraChip.Unknown"/>. <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="args"/> is longer than 48 bytes, or
    /// <paramref name="payload"/> exceeds <see cref="MaximumPayloadLength"/>.
    /// <para><paramref name="args"/> 超过 48 字节，或 <paramref name="payload"/> 超过
    /// <see cref="MaximumPayloadLength"/>。</para></exception>
    public static TegraRcmMessage Create(
        TegraChip chip,
        TegraRcmOpcode opcode,
        ReadOnlySpan<byte> args = default,
        ReadOnlySpan<byte> payload = default)
    {
        var layout = TegraRcmHeaderLayout.For(chip);
        if (args.Length > TegraConstants.ArgsLength)
        {
            throw new ArgumentException(
                $"RCM args are exactly {TegraConstants.ArgsLength} bytes, but {args.Length} were supplied.",
                nameof(args));
        }

        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentException(
                $"An RCM payload is at most {MaximumPayloadLength} bytes, but {payload.Length} were supplied.",
                nameof(payload));
        }

        var frameLength = TegraRcmProtocol.GetFrameLength(layout.HeaderLength, payload.Length);
        var frame = new byte[frameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frameLength);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(layout.OpcodeOffset, 4), (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(layout.LengthSecureOffset, 4), (uint)frameLength);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(layout.PayloadLengthOffset, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(layout.RcmVersionOffset, 4), layout.RcmVersion);
        args.CopyTo(frame.AsSpan(layout.ArgsOffset, args.Length));
        payload.CopyTo(frame.AsSpan(layout.HeaderLength, payload.Length));

        // The reference leaves the modulus, ECID and object signature of an unsigned message untouched; writing
        // zeros instead keeps every frame deterministic and reproducible.
        // 参考实现对未签名消息的模数、ECID 与对象签名不做初始化；改写为零可让每一帧确定且可复现。
        WritePadding(frame.AsSpan(layout.PaddingOffset, TegraConstants.HeaderPaddingLength));
        WritePadding(frame.AsSpan(layout.HeaderLength + payload.Length));

        return new TegraRcmMessage(layout, frame, payload.Length);
    }

    /// <summary>
    /// Parses a complete host message from its wire bytes: the header, the payload it declares and the padding.
    /// <para>从线上字节解析完整的主机消息：头部、头部声明的负载与填充。</para>
    /// </summary>
    /// <param name="frame">The message bytes; a longer buffer is allowed and only the declared length is consumed.
    /// <para>消息字节；允许传入更长的缓冲区，只消费声明的长度。</para></param>
    /// <returns>The parsed message. <para>解析出的消息。</para></returns>
    /// <exception cref="InvalidDataException">The bytes cannot be a message of any known layout: no
    /// <c>rcm_version</c> word matches its layout's offset, <c>len_insecure</c> is smaller than the header or larger
    /// than <paramref name="frame"/>, or <c>payload_len</c> does not fit the declared length.
    /// <para>这些字节不可能是任何已知布局的消息：任一布局偏移处的 <c>rcm_version</c> 字都不匹配，<c>len_insecure</c>
    /// 小于头部或大于 <paramref name="frame"/>，或 <c>payload_len</c> 与声明长度不兼容。</para>
    /// </exception>
    public static TegraRcmMessage ParseHostFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < TegraRcmHeaderLayout.Rcm1.HeaderLength)
        {
            throw new InvalidDataException(
                $"An RCM message is at least {TegraRcmHeaderLayout.Rcm1.HeaderLength} bytes, but {frame.Length} were supplied.");
        }

        var lengthInsecure = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (lengthInsecure < (uint)TegraRcmHeaderLayout.Rcm1.HeaderLength)
        {
            throw new InvalidDataException(
                $"RCM len_insecure {lengthInsecure} is shorter than the smallest RCM header.");
        }

        if (lengthInsecure > frame.Length)
        {
            throw new InvalidDataException(
                $"RCM len_insecure {lengthInsecure} exceeds the {frame.Length} bytes supplied.");
        }

        // The layout cannot be chosen from rcm_version alone, because that field's offset differs per layout. The
        // probe therefore matches from the outside in and only looks at offsets the supplied bytes actually reach.
        // 布局不能只按 rcm_version 选定，因为该字段的偏移随布局变化。因此自外向内探测，且只读取已提供字节
        // 覆盖到的偏移。
        var layout = ResolveLayout(frame, lengthInsecure);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
            frame.Slice(layout.PayloadLengthOffset, 4));
        if (payloadLength > lengthInsecure - (uint)layout.HeaderLength)
        {
            throw new InvalidDataException(
                $"RCM payload_len {payloadLength} does not fit the {lengthInsecure}-byte message for the " +
                $"0x{layout.HeaderLength:X}-byte header layout.");
        }

        var message = new TegraRcmMessage(layout, frame[..(int)lengthInsecure].ToArray(), (int)payloadLength);
        return message;
    }

    /// <summary>
    /// Serialises the message into the complete host frame: header, payload and payload padding.
    /// <para>把消息序列化为完整的主机帧：头部、负载与负载填充。</para>
    /// </summary>
    /// <returns>A copy of the wire bytes whose length equals <see cref="LengthInsecure"/>.
    /// <para>线上字节的副本，长度等于 <see cref="LengthInsecure"/>。</para></returns>
    public byte[] ToBytes() => (byte[])_frame.Clone();

    private static TegraRcmHeaderLayout ResolveLayout(ReadOnlySpan<byte> frame, uint lengthInsecure)
    {
        if (Matches(frame, lengthInsecure, TegraRcmHeaderLayout.Rcm1))
        {
            return TegraRcmHeaderLayout.Rcm1;
        }

        if (Matches(frame, lengthInsecure, TegraRcmHeaderLayout.Rcm40))
        {
            return TegraRcmHeaderLayout.Rcm40;
        }

        if (Matches(frame, lengthInsecure, TegraRcmHeaderLayout.Rcm35))
        {
            return TegraRcmHeaderLayout.Rcm35;
        }

        throw new InvalidDataException(
            "RCM header matches no known layout: no layout's rcm_version offset holds its own version word and no " +
            "layout fits the declared length.");
    }

    private static bool Matches(ReadOnlySpan<byte> frame, uint lengthInsecure, TegraRcmHeaderLayout layout)
        => lengthInsecure >= (uint)layout.HeaderLength
            && frame.Length >= layout.RcmVersionOffset + sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(frame[layout.RcmVersionOffset..]) == layout.RcmVersion;

    private static void WritePadding(Span<byte> padding)
    {
        padding.Clear();
        if (!padding.IsEmpty)
        {
            padding[0] = TegraConstants.PaddingFillByte;
        }
    }
}

/// <summary>
/// Reply a Tegra boot ROM sends for an RCM command: either a 4-byte status or version word, or a 16-byte error
/// record whose upper 32 bits carry <c>(error_code &lt;&lt; 16) | message_length</c>.
/// <para>Tegra boot ROM 对某条 RCM 命令的应答：或为 4 字节状态/版本字，或为 16 字节错误记录，其高 32 位携带
/// <c>(error_code &lt;&lt; 16) | message_length</c>。</para>
/// </summary>
/// <param name="Value">The first 32-bit word, read little-endian. <para>首个 32 位字，按小端读取。</para></param>
/// <param name="Length">The reply length: 4 or 16 bytes. <para>应答长度：4 或 16 字节。</para></param>
/// <param name="Auxiliary">The second 32-bit word, or zero for a 4-byte reply. <para>第二个 32 位字；4 字节应答为零。</para></param>
public readonly record struct TegraRcmReply(uint Value, int Length, uint Auxiliary)
{
    /// <summary>
    /// Parses a boot ROM reply. The device answers with either a bare status word or a 16-byte error record, so
    /// both shapes are accepted and any other length is rejected as untrusted input.
    /// <para>解析 boot ROM 应答。设备或返回裸状态字，或返回 16 字节错误记录，因此两种形态都被接受，其它长度一律按
    /// 不可信输入拒绝。</para>
    /// </summary>
    /// <param name="reply">The received bytes. <para>接收到的字节。</para></param>
    /// <returns>The parsed reply. <para>解析出的应答。</para></returns>
    /// <exception cref="InvalidDataException"><paramref name="reply"/> is neither 4 nor 16 bytes.
    /// <para><paramref name="reply"/> 既不是 4 字节也不是 16 字节。</para></exception>
    public static TegraRcmReply Parse(ReadOnlySpan<byte> reply) => reply.Length switch
    {
        MinimumLength => new TegraRcmReply(BinaryPrimitives.ReadUInt32LittleEndian(reply), MinimumLength, 0),
        ErrorRecordLength => new TegraRcmReply(
            BinaryPrimitives.ReadUInt32LittleEndian(reply),
            ErrorRecordLength,
            BinaryPrimitives.ReadUInt32LittleEndian(reply[4..])),
        _ => throw new InvalidDataException(
            $"An RCM reply is {MinimumLength} or {ErrorRecordLength} bytes, but {reply.Length} were received."),
    };

    /// <summary>
    /// Length of a bare status or version reply.
    /// <para>裸状态或版本应答的长度。</para>
    /// </summary>
    public const int MinimumLength = 4;

    /// <summary>
    /// Length of an error reply record; the error text follows it.
    /// <para>错误应答记录的长度；错误文本随后发送。</para>
    /// </summary>
    public const int ErrorRecordLength = 16;

    /// <summary>
    /// Gets the major half of a 4-byte version reply (<c>RCM_VERSION_MAJOR</c>).
    /// <para>获取 4 字节版本应答的高半部分（<c>RCM_VERSION_MAJOR</c>）。</para>
    /// </summary>
    public uint VersionMajor => TegraConstants.GetRcmVersionMajor(Value);

    /// <summary>
    /// Gets the minor half of a 4-byte version reply, which carries the RCM version the device speaks
    /// (<c>RCM_VERSION_MINOR</c>). <para>获取 4 字节版本应答的低半部分，即设备使用的 RCM 版本
    /// （<c>RCM_VERSION_MINOR</c>）。</para>
    /// </summary>
    public uint VersionMinor => TegraConstants.GetRcmVersionMinor(Value);

    /// <summary>
    /// Gets the error code of a 16-byte error reply, or <see langword="null"/> for a 4-byte reply.
    /// <para>获取 16 字节错误应答的错误码；4 字节应答为 <see langword="null"/>。</para>
    /// </summary>
    public ushort? ErrorCode => Length == ErrorRecordLength ? (ushort)(Auxiliary >> 16) : null;

    /// <summary>
    /// Gets the length of the device-generated error text that follows a 16-byte error reply, or
    /// <see langword="null"/> for a 4-byte reply. <para>获取 16 字节错误应答之后设备生成错误文本的长度；4 字节应答为
    /// <see langword="null"/>。</para>
    /// </summary>
    public ushort? MessageLength => Length == ErrorRecordLength ? (ushort)(Auxiliary & 0xFFFF) : null;

    /// <summary>
    /// Gets whether this is a bare status or version reply rather than an error record.
    /// <para>获取本应答是否为裸状态/版本应答而不是错误记录。</para>
    /// </summary>
    public bool IsStatusOnly => Length == MinimumLength;
}
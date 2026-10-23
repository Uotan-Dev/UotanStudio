namespace UotanStudio.Mirroring;

/// <summary>
/// H.264 access-unit helpers for the pushed device capture library: parameter-set discovery, key-frame
/// detection and SPS geometry parsing.
/// <para>推送的设备采集库所使用的 H.264 访问单元辅助方法：参数集发现、关键帧检测与 SPS 几何解析。</para>
/// </summary>
/// <remarks>
/// <para>
/// The gRPC video stream carries Annex-B access units without a separate codec-configuration message, so the
/// client derives the H.264 parameter sets and frame geometry from the first access unit that contains them.
/// All parsers operate on bounded spans and reject truncated or oversized input instead of guessing.
/// </para>
/// <para>
/// gRPC 视频流承载 Annex-B 访问单元，没有独立的编码配置消息，因此客户端从首个包含参数集的访问单元推导
/// H.264 参数集与帧几何。所有解析器都在有界区间上工作，遇到截断或超长输入直接拒绝而不是猜测。
/// </para>
/// </remarks>
public static class OhscrcpyProtocol
{
    /// <summary>Maximum accepted H.264 dimension on either axis. <para>任一轴上可接受的 H.264 尺寸上限。</para></summary>
    public const int MaximumDimension = 16384;

    /// <summary>Builds an Annex-B codec configuration from SPS and PPS NAL units.
    /// <para>由 SPS 与 PPS NAL 单元构建 Annex-B 编码配置。</para>
    /// </summary>
    /// <param name="sps">SPS NAL unit including its NAL header byte but excluding a start code.
    /// <para>包含 NAL 头字节但不含起始码的 SPS NAL 单元。</para></param>
    /// <param name="pps">PPS NAL unit including its NAL header byte but excluding a start code.
    /// <para>包含 NAL 头字节但不含起始码的 PPS NAL 单元。</para></param>
    /// <returns>The start-code-prefixed SPS followed by the start-code-prefixed PPS.
    /// <para>带起始码前缀的 SPS，后接带起始码前缀的 PPS。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="sps"/> or <paramref name="pps"/> is
    /// <see langword="null"/>. <para><paramref name="sps"/> 或 <paramref name="pps"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] BuildAnnexBConfiguration(byte[] sps, byte[] pps)
    {
        ArgumentNullException.ThrowIfNull(sps);
        ArgumentNullException.ThrowIfNull(pps);
        var result = new byte[8 + sps.Length + pps.Length];
        result[3] = 1;
        sps.CopyTo(result, 4);
        result[4 + sps.Length + 3] = 1;
        pps.CopyTo(result, 8 + sps.Length);
        return result;
    }

    /// <summary>Determines whether an Annex-B access unit contains an IDR slice.
    /// <para>判断 Annex-B 访问单元是否包含 IDR 切片。</para>
    /// </summary>
    /// <param name="accessUnit">Access-unit bytes, possibly empty. <para>访问单元字节，可为空。</para></param>
    /// <returns><see langword="true"/> when an IDR (NAL type 5) unit is present.
    /// <para>存在 IDR（NAL 类型 5）单元时为 <see langword="true"/>。</para></returns>
    public static bool IsKeyFrame(ReadOnlySpan<byte> accessUnit)
    {
        var position = 0;
        while (TryFindStartCode(accessUnit, position, out var start, out var headerLength))
        {
            var nalStart = start + headerLength;
            if (nalStart >= accessUnit.Length) return false;
            if ((accessUnit[nalStart] & 0x1F) == 5) return true;
            position = nalStart;
        }

        return false;
    }

    /// <summary>Extracts the SPS and PPS NAL units from an Annex-B access unit.
    /// <para>从 Annex-B 访问单元中提取 SPS 与 PPS NAL 单元。</para>
    /// </summary>
    /// <param name="accessUnit">Access-unit bytes, possibly empty. <para>访问单元字节，可为空。</para></param>
    /// <param name="sps">The copied SPS including its NAL header, or an empty array when absent.
    /// <para>复制出的、包含 NAL 头的 SPS；缺失时为空数组。</para></param>
    /// <param name="pps">The copied PPS including its NAL header, or an empty array when absent.
    /// <para>复制出的、包含 NAL 头的 PPS；缺失时为空数组。</para></param>
    /// <returns><see langword="true"/> when both parameter sets were found.
    /// <para>两个参数集都找到时为 <see langword="true"/>。</para></returns>
    public static bool TryGetParameterSets(ReadOnlySpan<byte> accessUnit, out byte[] sps, out byte[] pps)
    {
        sps = [];
        pps = [];
        var position = 0;
        byte[]? foundSps = null;
        byte[]? foundPps = null;
        while (TryFindStartCode(accessUnit, position, out var start, out var headerLength))
        {
            var nalStart = start + headerLength;
            if (nalStart >= accessUnit.Length) break;
            var end = accessUnit.Length;
            if (TryFindStartCode(accessUnit, nalStart, out var nextStart, out _)) end = nextStart;
            var nal = accessUnit[nalStart..end];
            switch (nal[0] & 0x1F)
            {
                case 7:
                    foundSps = TrimTrailingZeros(nal).ToArray();
                    break;
                case 8:
                    foundPps = TrimTrailingZeros(nal).ToArray();
                    break;
            }

            if (foundSps is not null && foundPps is not null)
            {
                sps = foundSps;
                pps = foundPps;
                return true;
            }

            position = end;
        }

        sps = foundSps ?? [];
        pps = foundPps ?? [];
        return false;
    }

    /// <summary>Parses the coded picture geometry from an H.264 sequence parameter set.
    /// <para>从 H.264 序列参数集解析编码图像几何。</para>
    /// </summary>
    /// <param name="sps">SPS NAL unit including its NAL header byte but excluding a start code.
    /// <para>包含 NAL 头字节但不含起始码的 SPS NAL 单元。</para></param>
    /// <param name="width">Coded width in pixels. <para>编码宽度（像素）。</para></param>
    /// <param name="height">Coded height in pixels. <para>编码高度（像素）。</para></param>
    /// <returns><see langword="true"/> when the geometry was parsed and lies within
    /// <see cref="MaximumDimension"/>. <para>几何解析成功且位于 <see cref="MaximumDimension"/> 以内时为
    /// <see langword="true"/>。</para></returns>
    public static bool TryParseSpsDimensions(ReadOnlySpan<byte> sps, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (sps.Length < 5) return false;
        try
        {
            var rbsp = RemoveEmulationPrevention(sps[1..]);
            var reader = new BitReader(rbsp);
            var profileIdc = reader.ReadBits(8);
            reader.ReadBits(8);
            reader.ReadBits(8);
            reader.ReadUnsignedExpGolomb();
            var chromaFormatIdc = 1;
            var separateColourPlane = false;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = checked((int)reader.ReadUnsignedExpGolomb());
                if (chromaFormatIdc > 3) return false;
                if (chromaFormatIdc == 3) separateColourPlane = reader.ReadBit();
                reader.ReadUnsignedExpGolomb();
                reader.ReadUnsignedExpGolomb();
                reader.ReadBit();
                if (reader.ReadBit()) SkipScalingLists(ref reader, chromaFormatIdc);
            }

            reader.ReadUnsignedExpGolomb();
            var pictureOrderCountType = reader.ReadUnsignedExpGolomb();
            switch (pictureOrderCountType)
            {
                case 0:
                    reader.ReadUnsignedExpGolomb();
                    break;
                case 1:
                    reader.ReadBit();
                    reader.ReadSignedExpGolomb();
                    reader.ReadSignedExpGolomb();
                    var cycle = reader.ReadUnsignedExpGolomb();
                    if (cycle > 256) return false;
                    for (var index = 0u; index < cycle; index++) reader.ReadSignedExpGolomb();
                    break;
                case 2:
                    break;
                default:
                    return false;
            }

            reader.ReadUnsignedExpGolomb();
            reader.ReadBit();
            var widthInMacroblocks = checked((long)reader.ReadUnsignedExpGolomb() + 1);
            var heightInMapUnits = checked((long)reader.ReadUnsignedExpGolomb() + 1);
            var frameMbsOnly = reader.ReadBit();
            if (!frameMbsOnly) reader.ReadBit();
            reader.ReadBit();
            long cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (reader.ReadBit())
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            var cropUnitX = chromaFormatIdc == 0 || separateColourPlane ? 1 : chromaFormatIdc == 3 ? 1 : 2;
            var cropUnitY = (chromaFormatIdc == 0 || separateColourPlane ? 1 : chromaFormatIdc == 1 ? 2 : 1)
                * (frameMbsOnly ? 1 : 2);
            var parsedWidth = widthInMacroblocks * 16 - cropUnitX * (cropLeft + cropRight);
            var parsedHeight = heightInMapUnits * 16 * (frameMbsOnly ? 1 : 2) - cropUnitY * (cropTop + cropBottom);
            if (parsedWidth is < 1 or > MaximumDimension || parsedHeight is < 1 or > MaximumDimension) return false;
            width = (int)parsedWidth;
            height = (int)parsedHeight;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void SkipScalingLists(ref BitReader reader, int chromaFormatIdc)
    {
        var count = chromaFormatIdc == 3 ? 12 : 8;
        for (var list = 0; list < count; list++)
        {
            if (!reader.ReadBit()) continue;
            var size = list < 6 ? 16 : 64;
            var lastScale = 8;
            var nextScale = 8;
            for (var index = 0; index < size; index++)
            {
                if (nextScale != 0)
                {
                    var delta = reader.ReadSignedExpGolomb();
                    nextScale = (lastScale + delta + 256) % 256;
                }

                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }
    }

    private static ReadOnlySpan<byte> TrimTrailingZeros(ReadOnlySpan<byte> nal)
    {
        var length = nal.Length;
        while (length > 1 && nal[length - 1] == 0) length--;
        return nal[..length];
    }

    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int from, out int start, out int headerLength)
    {
        for (var index = Math.Max(from, 0); index + 3 <= data.Length; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0) continue;
            if (data[index + 2] == 1)
            {
                start = index;
                headerLength = 3;
                return true;
            }

            if (index + 4 <= data.Length && data[index + 2] == 0 && data[index + 3] == 1)
            {
                start = index;
                headerLength = 4;
                return true;
            }
        }

        start = -1;
        headerLength = 0;
        return false;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var length = 0;
        var zeros = 0;
        foreach (var value in data)
        {
            if (zeros >= 2 && value == 0x03)
            {
                zeros = 0;
                continue;
            }

            output[length++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return output[..length];
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        internal BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        internal bool ReadBit()
        {
            if (_bit >= _data.Length * 8) throw new InvalidDataException("The SPS bitstream ended early.");
            var value = (_data[_bit >> 3] >> (7 - (_bit & 7))) & 1;
            _bit++;
            return value != 0;
        }

        internal uint ReadBits(int count)
        {
            if (count is < 0 or > 32) throw new InvalidDataException("Invalid bit count.");
            uint value = 0;
            for (var index = 0; index < count; index++) value = (value << 1) | (ReadBit() ? 1u : 0u);
            return value;
        }

        internal uint ReadUnsignedExpGolomb()
        {
            var leadingZeros = 0;
            while (!ReadBit())
            {
                if (++leadingZeros > 31) throw new InvalidDataException("Exp-Golomb code is too long.");
            }

            return leadingZeros == 0 ? 0 : ((1u << leadingZeros) - 1) + ReadBits(leadingZeros);
        }

        internal int ReadSignedExpGolomb()
        {
            var value = ReadUnsignedExpGolomb();
            return (value & 1) == 1 ? (int)((value + 1) / 2) : -(int)(value / 2);
        }
    }
}

/// <summary>
/// H.264 parameter sets announced by the first captured access unit.
/// <para>首个采集访问单元宣告的 H.264 参数集。</para>
/// </summary>
/// <param name="Width">Coded width in pixels. <para>编码宽度（像素）。</para></param>
/// <param name="Height">Coded height in pixels. <para>编码高度（像素）。</para></param>
/// <param name="Sps">SPS NAL unit without a start code. <para>不含起始码的 SPS NAL 单元。</para></param>
/// <param name="Pps">PPS NAL unit without a start code. <para>不含起始码的 PPS NAL 单元。</para></param>
public sealed record OhscrcpyVideoConfig(int Width, int Height, byte[] Sps, byte[] Pps)
{
    /// <summary>Whether the stream carries H.264 Annex-B access units.
    /// <para>码流是否承载 H.264 Annex-B 访问单元。</para></summary>
    /// <value>Always <see langword="true"/>; the pushed library captures H.264 only.
    /// <para>始终为 <see langword="true"/>；推送的库只采集 H.264。</para></value>
    public bool IsH264 => true;

    /// <summary>Codec name used in diagnostics. <para>用于诊断的编码名称。</para></summary>
    /// <value>The constant <c>H264</c>. <para>常量 <c>H264</c>。</para></value>
    public string CodecName => "H264";

    /// <summary>Builds the Annex-B codec configuration the decoder pipeline consumes.
    /// <para>构建解码管线消费的 Annex-B 编码配置。</para>
    /// </summary>
    /// <returns>The start-code-prefixed SPS and PPS. <para>带起始码前缀的 SPS 与 PPS。</para></returns>
    public byte[] BuildAnnexBConfiguration() => OhscrcpyProtocol.BuildAnnexBConfiguration(Sps, Pps);
}
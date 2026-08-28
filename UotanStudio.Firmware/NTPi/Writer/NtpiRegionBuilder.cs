using System.Buffers;
using System.Text;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>负责 NTPI 打包过程中区域 1-5 的构建与加密。</para>
/// Builds and encrypts regions 1-5 during NTPI packing.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者初始化 NtpiRegionBuilder 的新实例。</para>
/// Initializes a new instance of NtpiRegionBuilder with the specified crypto provider.
/// </remarks>
internal class NtpiRegionBuilder(ICryptoProvider cryptoProvider)
{
    private const int RegionBlockHeaderSize = 40;
    private static readonly byte[] NtpiMagic = Encoding.ASCII.GetBytes("NTPI");
    private static readonly byte[] MetadataPayload = NtpiXmlGenerator.GenerateMinimalXml("metadata");
    private static readonly byte[] PatchPayload = NtpiXmlGenerator.GenerateMinimalXml("patch");
    private static readonly byte[] RawProgramPayload = NtpiXmlGenerator.GenerateMinimalXml("rawprogram");

    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));

    /// <summary>
    /// <para>表示区域构建的结果。</para>
    /// Represents the result of region building.
    /// </summary>
    public readonly struct BuildResult
    {
        /// <summary>构建的 NTPI 头部。 <para>The built NTPI header.</para></summary>
        public NtpiHeader Header { get; init; }

        /// <summary>加密后的区域 1-5。 <para>Encrypted regions 1-5.</para></summary>
        public byte[][] EncryptedRegions { get; init; }
    }

    /// <summary>
    /// <para>构建区域 1-5 并加密。Region6 由写入器流式写出,此处仅记录其长度。</para>
    /// Builds and encrypts regions 1-5. Region6 is streamed out by the writer; only its length is recorded here.
    /// </summary>
    public BuildResult BuildRegions(
        IReadOnlyList<FileEntry> fileEntries,
        long region6Length,
        byte[] keyMap,
        Dictionary<int, NtpiVersionKey> versionKeys,
        ulong major,
        ulong minor,
        ulong patch,
        Dictionary<RegionType, byte[]>? customRegionPayloads)
    {
        ArgumentNullException.ThrowIfNull(fileEntries);
        ArgumentNullException.ThrowIfNull(keyMap);
        ArgumentNullException.ThrowIfNull(versionKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(region6Length);

        var fileIndexPayload = NtpiXmlGenerator.GenerateFileIndexXml(fileEntries);

        var metadataPayload = customRegionPayloads is not null && customRegionPayloads.TryGetValue(RegionType.Metadata, out var customMeta)
            ? customMeta : MetadataPayload;
        var patchPayload = customRegionPayloads is not null && customRegionPayloads.TryGetValue(RegionType.Patch, out var customPatch)
            ? customPatch : PatchPayload;
        var rawProgramPayload = customRegionPayloads is not null && customRegionPayloads.TryGetValue(RegionType.RawProgram, out var customRaw)
            ? customRaw : RawProgramPayload;

        var regionPayloads = new (RegionType Type, byte[] Payload)[]
        {
            (RegionType.Metadata, metadataPayload),
            (RegionType.Patch, patchPayload),
            (RegionType.RawProgram, rawProgramPayload),
            (RegionType.KeyMap, keyMap),
            (RegionType.FileIndex, fileIndexPayload)
        };

        var encryptedSizes = new int[5];
        var finalEncryptedRegions = new byte[5][];
        var blockHeaderBuffer = ArrayPool<byte>.Shared.Rent(RegionBlockHeaderSize);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                encryptedSizes[i] = NtpiPaddingHelper.CalculatePaddedSize(RegionBlockHeaderSize + regionPayloads[i].Payload.Length);
            }

            for (var i = 0; i < 5; i++)
            {
                var (type, payload) = regionPayloads[i];
                var versionKey = versionKeys[(int)type];

                var nextHeader = i < 4
                    ? new RegionHeader
                    {
                        RegionType = (ulong)regionPayloads[i + 1].Type,
                        RegionSize = (ulong)encryptedSizes[i + 1]
                    }
                    : new RegionHeader
                    {
                        RegionType = (ulong)RegionType.Region6,
                        RegionSize = (ulong)region6Length
                    };
                var blockHeader = new RegionBlockHeader
                {
                    ThisHeader = new RegionHeader
                    {
                        RegionType = (ulong)type,
                        RegionSize = (ulong)encryptedSizes[i]
                    },
                    NextHeader = nextHeader,
                    RealSize = (ulong)payload.Length
                };

                StructHelper.WriteRegionBlockHeader(blockHeader, blockHeaderBuffer);

                var combinedSize = RegionBlockHeaderSize + payload.Length;
                var combined = ArrayPool<byte>.Shared.Rent(combinedSize);
                try
                {
                    Buffer.BlockCopy(blockHeaderBuffer, 0, combined, 0, RegionBlockHeaderSize);
                    Buffer.BlockCopy(payload, 0, combined, RegionBlockHeaderSize, payload.Length);

                    var padded = NtpiPaddingHelper.PadToBlockSize(combined, combinedSize);
                    finalEncryptedRegions[i] = _cryptoProvider.Encrypt(padded, versionKey.Key, versionKey.Iv);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(combined);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockHeaderBuffer);
        }

        var ntpiHeader = new NtpiHeader
        {
            Magic = NtpiMagic,
            Padding = 0,
            VersionMajor = major,
            VersionMinor = minor,
            VersionPatch = patch,
            FirstRegionHeader = new RegionHeader
            {
                RegionType = (ulong)RegionType.Metadata,
                RegionSize = (ulong)encryptedSizes[0]
            }
        };

        return new BuildResult
        {
            Header = ntpiHeader,
            EncryptedRegions = finalEncryptedRegions
        };
    }
}
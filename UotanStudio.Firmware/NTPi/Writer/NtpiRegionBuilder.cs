using System.Buffers;
using System.Text;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>负责 NTPI 打包过程中区域的构建与加密。</para>
/// Builds and encrypts regions during NTPI packing.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者初始化 NtpiRegionBuilder 的新实例。</para>
/// Initializes a new instance of NtpiRegionBuilder with the specified crypto provider.
/// </remarks>
internal class NtpiRegionBuilder(ICryptoProvider cryptoProvider)
{
    private const int NtEncodeHeaderSize = 112;
    private const int RegionBlockHeaderSize = 40;
    private static readonly byte[] NtEncodeMagic = Encoding.ASCII.GetBytes("NTENCODE");
    private static readonly byte[] NtpiMagic = Encoding.ASCII.GetBytes("NTPI");
    private static readonly byte[] MetadataPayload = NtpiXmlGenerator.GenerateMinimalXml("metadata");
    private static readonly byte[] PatchPayload = NtpiXmlGenerator.GenerateMinimalXml("patch");
    private static readonly byte[] RawProgramPayload = NtpiXmlGenerator.GenerateMinimalXml("rawprogram");
    private static readonly byte[] ZeroKey32 = new byte[32];
    private static readonly byte[] ZeroIv32 = new byte[16];

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

        /// <summary>Region6 数据。 <para>Region6 data.</para></summary>
        public byte[] Region6Data { get; init; }

        /// <summary>Region6 有效长度。 <para>Region6 valid length.</para></summary>
        public int Region6Length { get; init; }
    }

    /// <summary>
    /// <para>构建所有区域并加密区域 1-5。</para>
    /// Builds all regions and encrypts regions 1-5.
    /// </summary>
    public BuildResult BuildRegions(
        NtpiFileProcessor.ProcessedFile?[] processedResults,
        int fileCount,
        byte[] keyMap,
        Dictionary<int, NtpiVersionKey> versionKeys,
        ulong major,
        ulong minor,
        ulong patch,
        Dictionary<RegionType, byte[]>? customRegionPayloads)
    {
        // Assemble Region6 from processed results.
        var processedEntries = new List<FileEntry>();
        var encodeHeaderBuffer = ArrayPool<byte>.Shared.Rent(NtEncodeHeaderSize);
        try
        {
            long estimatedRegion6Size = 0;
            for (var i = 0; i < fileCount; i++)
            {
                var r = processedResults[i];
                if (r != null)
                {
                    estimatedRegion6Size += 112 + r.Value.Encrypted.Length;
                }
            }
            using var region6Stream = new MemoryStream((int)estimatedRegion6Size);

            for (var i = 0; i < fileCount; i++)
            {
                if (processedResults[i] is not { } result)
                {
                    continue;
                }

                var fileOffset = region6Stream.Position;

                var encodeHeader = new NtEncodeHeader
                {
                    Magic = NtEncodeMagic,
                    PrimaryType = 1,
                    CompressSubtype = 1,
                    EncryptSubtype = 1,
                    Padding = 0,
                    ProcessedSize = result.ProcessedSize,
                    OriginalSize = (ulong)result.Encrypted.Length,
                    Key = ZeroKey32,
                    Iv = ZeroIv32,
                    KeySize = 32,
                    IvSize = 16
                };

                StructHelper.WriteNtEncodeHeader(encodeHeader, encodeHeaderBuffer);
                region6Stream.Write(encodeHeaderBuffer, 0, NtEncodeHeaderSize);
                region6Stream.Write(result.Encrypted, 0, result.Encrypted.Length);

                var totalLength = region6Stream.Position - fileOffset;

                processedEntries.Add(new FileEntry
                {
                    Name = result.Name,
                    OriginalLength = result.DataLength,
                    Length = totalLength,
                    FileSha256Hash = result.Sha256Hash,
                    KeyIndex = result.KeyIndex,
                    Offset = fileOffset
                });
            }

            var region6Data = region6Stream.GetBuffer();
            var region6Length = (int)region6Stream.Length;

            var fileIndexPayload = NtpiXmlGenerator.GenerateFileIndexXml(processedEntries);

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
                EncryptedRegions = finalEncryptedRegions,
                Region6Data = region6Data,
                Region6Length = region6Length
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encodeHeaderBuffer);
        }
    }
}
using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Writer;

/// <summary>
/// <para>负责 NTPI 打包过程中文件的压缩与加密处理。</para>
/// Handles file compression and encryption during NTPI packing.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者和压缩器初始化 NtpiFileProcessor 的新实例。</para>
/// Initializes a new instance of NtpiFileProcessor with the specified crypto provider and compressor.
/// </remarks>
internal class NtpiFileProcessor(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
{
    private readonly ICryptoProvider _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
    private readonly ILzma2Compressor _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    /// <summary>
    /// <para>表示处理完成的文件(已压缩并加密)。</para>
    /// Represents a processed file (compressed and encrypted).
    /// </summary>
    public readonly struct ProcessedFile
    {
        /// <summary>加密后的数据。 <para>Encrypted data.</para></summary>
        public byte[] Encrypted { get; init; }

        /// <summary>处理后(加密)数据大小。 <para>Processed (encrypted) data size.</para></summary>
        public ulong ProcessedSize { get; init; }

        /// <summary>文件名称。 <para>File name.</para></summary>
        public string Name { get; init; }

        /// <summary>原始数据长度。 <para>Original data length.</para></summary>
        public int DataLength { get; init; }

        /// <summary>密钥索引。 <para>Key index.</para></summary>
        public int KeyIndex { get; init; }

        /// <summary>SHA-256 哈希。 <para>SHA-256 hash.</para></summary>
        public string Sha256Hash { get; init; }
    }

    /// <summary>
    /// <para>并行压缩并加密文件列表。</para>
    /// Compresses and encrypts the file list in parallel.
    /// </summary>
    public ProcessedFile?[] ProcessFiles(
        List<FileEntry> entryList,
        Dictionary<string, byte[]> fileData,
        KeyMapKeyProvider keyMapProvider,
        byte[] zeroIv,
        int[] keyIndices,
        int maxDegreeOfParallelism,
        IProgress<NtpiPackProgress>? progress)
    {
        var results = new ProcessedFile?[entryList.Count];
        var parallelism = Math.Max(1, maxDegreeOfParallelism);
        var processed = 0;
        var sync = new object();

        void ProcessOne(int i)
        {
            var entry = entryList[i];
            if (!fileData.TryGetValue(entry.Name, out var data) || keyIndices[i] < 0)
            {
                return;
            }

            var compressed = _compressor.Compress(data, (ulong)data.Length);
            var padded = NtpiPaddingHelper.PadToBlockSize(compressed, compressed.Length);
            var blockKey = keyMapProvider.GetKey(keyIndices[i], 0);
            var encrypted = _cryptoProvider.Encrypt(padded, blockKey, zeroIv);

            results[i] = new ProcessedFile
            {
                Encrypted = encrypted,
                ProcessedSize = (ulong)encrypted.Length,
                Name = entry.Name,
                DataLength = data.Length,
                KeyIndex = keyIndices[i],
                Sha256Hash = StructHelper.ComputeSha256Hex(data)
            };

            lock (sync)
            {
                processed++;
                progress?.Report(new NtpiPackProgress
                {
                    TotalFiles = entryList.Count,
                    CompletedFiles = processed,
                    CurrentFileName = entry.Name,
                    Phase = NtpiPackPhase.ProcessingFiles
                });
            }
        }

        if (parallelism <= 1 || entryList.Count <= 1)
        {
            for (var i = 0; i < entryList.Count; i++)
            {
                ProcessOne(i);
            }
        }
        else
        {
            Parallel.For(0, entryList.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, ProcessOne);
        }

        return results;
    }
}
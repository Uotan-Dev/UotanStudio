using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>OFP MTK 文件解析器，提供纯解析功能（不包含加密操作）。</para>
/// OFP MTK file parser providing pure parsing functionality (no crypto operations).
/// </summary>
internal static class OfpMtkParser
{
    /// <summary>
    /// <para>解析 OFP MTK 文件表条目。</para>
    /// Parses OFP MTK file table entries.
    /// </summary>
    /// <param name="stream"><para>可读取的文件流。</para>A readable file stream.</param>
    /// <param name="fileSize"><para>文件大小（字节）。</para>File size in bytes.</param>
    /// <param name="entryCount"><para>文件表条目数量，由 ParseMetadata 提供。</para>The file table entry count, provided by ParseMetadata.</param>
    /// <returns><para>解析后的条目列表。</para>The parsed entry list.</returns>
    internal static IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, int entryCount)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        try
        {
            if (entryCount is <= 0 or > 10000)
            {
                return [];
            }

            var fileTableSize = entryCount * OfpMtkFileEntry.Size;
            var fileTableOffset = fileSize - OfpMtkHeader.Size - fileTableSize;

            if (fileTableOffset < 0)
            {
                return [];
            }

            stream.Position = fileTableOffset;

            // Read the entire file table as one block, then apply MtkShuffle to the whole block.
            // This is critical because MtkShuffle uses key cycling (key[i % keyLength]),
            // and the key position must be continuous across all entries.
            var fileTableBuffer = new byte[fileTableSize];
            var totalRead = 0;
            while (totalRead < fileTableSize)
            {
                var bytesRead = stream.Read(fileTableBuffer, totalRead, fileTableSize - totalRead);
                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;
            }

            if (totalRead != fileTableSize)
            {
                return [];
            }

            OppMtkKeyDerivation.MtkShuffle(MtkKeyDatabase.MtkHeaderShuffleKey, fileTableBuffer);

            var entries = new List<OppEntry>(entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                var entryData = new ReadOnlySpan<byte>(fileTableBuffer, i * OfpMtkFileEntry.Size, OfpMtkFileEntry.Size);
                var fileEntry = OfpMtkFileEntry.Parse(entryData);

                entries.Add(new OppEntry
                {
                    Name = fileEntry.Name,
                    Offset = fileEntry.StartOffset,
                    Size = fileEntry.TotalLength,
                    EncryptedSize = fileEntry.EncryptedLength,
                    IsCompressed = false,
                    FileName = fileEntry.FileName
                });
            }

            return entries;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// <para>从文件流中读取 OFP MTK 主头结构。</para>
    /// Reads the OFP MTK main header structure from the file stream.
    /// </summary>
    /// <param name="stream"><para>可读取的文件流。</para>A readable file stream.</param>
    /// <param name="fileSize"><para>文件大小（字节）。</para>File size in bytes.</param>
    /// <returns><para>解析后的主头结构。</para>The parsed main header structure.</returns>
    internal static OfpMtkHeader ReadMainHeader(Stream stream, long fileSize)
    {
        if (fileSize < OfpMtkHeader.Size)
        {
            throw new InvalidOppFormatException("File too small for MTK header.");
        }

        stream.Position = fileSize - OfpMtkHeader.Size;
        var headerBuffer = new byte[OfpMtkHeader.Size];
        var bytesRead = stream.Read(headerBuffer, 0, OfpMtkHeader.Size);
        if (bytesRead != OfpMtkHeader.Size)
        {
            throw new InvalidOppFormatException("Failed to read MTK header.");
        }

        // Apply MTK Shuffle with key "geyixue" to deobfuscate
        OppMtkKeyDerivation.MtkShuffle(MtkKeyDatabase.MtkHeaderShuffleKey, headerBuffer);
        var headerData = new ReadOnlySpan<byte>(headerBuffer);
        return OfpMtkHeader.Parse(headerData);
    }
}
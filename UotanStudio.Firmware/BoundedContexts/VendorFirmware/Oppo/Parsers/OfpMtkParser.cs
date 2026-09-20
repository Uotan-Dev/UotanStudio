using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;

internal static class OfpMtkParser
{
    /// <summary>
    /// Parses OFP MTK file table entries.
    /// <para>解析 OFP MTK 文件表条目。</para>
    /// </summary>
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
    /// Reads the OFP MTK main header structure from the file stream.
    /// <para>从文件流中读取 OFP MTK 主头结构。</para>
    /// </summary>
    internal static OfpMtkHeader ReadMainHeader(Stream stream, long fileSize)
    {
        if (fileSize < OfpMtkHeader.Size)
        {
            throw new InvalidOppFormatException("File too small for MTK header.");
        }

        stream.Position = fileSize - OfpMtkHeader.Size;
        var headerBuffer = new byte[OfpMtkHeader.Size];
        var bytesRead = stream.ReadAtLeast(headerBuffer, OfpMtkHeader.Size, throwOnEndOfStream: false);
        if (bytesRead != OfpMtkHeader.Size)
        {
            throw new InvalidOppFormatException("Failed to read MTK header.");
        }


        OppMtkKeyDerivation.MtkShuffle(MtkKeyDatabase.MtkHeaderShuffleKey, headerBuffer);
        var headerData = new ReadOnlySpan<byte>(headerBuffer);
        return OfpMtkHeader.Parse(headerData);
    }
}
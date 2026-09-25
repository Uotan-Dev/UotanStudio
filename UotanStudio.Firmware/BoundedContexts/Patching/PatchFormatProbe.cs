using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;
using UotanStudio.Firmware.BoundedContexts.Executable.Elf;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Stable content identities. No filename or extension contributes to these results.</summary>
public enum PatchFormatKind
{
    Unknown, Json, Xml, Ini, Elf, Sparse, ExtFamily, Erofs, F2fs, Boot, VendorBoot, Vbmeta,
    Payload, Dtb, Dtbo, Dex, Zip, Cpio, Gzip, Xz, Zstd, Lz4, Bsdiff, Rockchip, Ops, Ozip, Ntpi, Amlogic
}

/// <summary>Signature recognition is deliberately distinct from structural validation.</summary>
public enum PatchFormatEvidence { Unknown, Signature, Structure }

/// <summary>Machine-readable inspection outcome; UI translates the evidence code rather than persisting translated labels.</summary>
public sealed record PatchFormatResult(PatchFormatKind Format, PatchFormatEvidence Evidence, string Detail, bool Rejected = false)
{
    /// <summary>Unknown and rejected inputs never satisfy a format precondition.</summary>
    public bool Matches(PatchFormatKind expected, PatchFormatEvidence minimum)
        => !Rejected && expected != PatchFormatKind.Unknown && Enum.IsDefined(expected)
            && minimum is PatchFormatEvidence.Signature or PatchFormatEvidence.Structure && Format == expected && Evidence >= minimum;
}

/// <summary>Bounded content detection and available structural checks for offline patch preconditions.</summary>
public static class PatchFormatProbe
{
    private const int ProbeSize = 4096;
    private const int MaxEntries = 100_000;

    /// <summary>
    /// Inspects a seekable stream and restores its position. An INI hint explicitly selects that ambiguous text dialect.
    /// Structural success checks metadata/layout, not cryptographic trust, decompressed content or runtime compatibility.
    /// </summary>
    public static PatchFormatResult Read(Stream stream, PatchFormatKind hint = PatchFormatKind.Unknown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Format probing requires a readable seekable stream.", nameof(stream));
        }
        if (!Enum.IsDefined(hint))
        {
            throw new ArgumentOutOfRangeException(nameof(hint));
        }
        var originalPosition = stream.Position;
        var kind = PatchFormatKind.Unknown;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            var header = new byte[(int)Math.Min(ProbeSize, stream.Length)];
            stream.ReadExactly(header);
            kind = Detect(header);
            if (hint == PatchFormatKind.Ini)
            {
                kind = PatchFormatKind.Ini;
            }
            if (kind is PatchFormatKind.Unknown or PatchFormatKind.Json or PatchFormatKind.Xml or PatchFormatKind.Ini)
            {
                var textKind = DetectText(header, out var whitespace);
                if (kind == PatchFormatKind.Ini || textKind != PatchFormatKind.Unknown || whitespace)
                {
                    kind = kind == PatchFormatKind.Ini ? kind : textKind;
                    var text = ReadText(stream, header, cancellationToken);
                    if (kind == PatchFormatKind.Unknown)
                    {
                        kind = DetectText(text.AsSpan());
                        if (kind == PatchFormatKind.Unknown)
                        {
                            return new PatchFormatResult(kind, PatchFormatEvidence.Unknown, "Unknown");
                        }
                    }
                    if (kind == PatchFormatKind.Json)
                    {
                        JsonPatch.Validate(text, cancellationToken);
                    }
                    else if (kind == PatchFormatKind.Ini)
                    {
                        IniPatch.Read(text, string.Empty, "__uotan_format_probe__", cancellationToken);
                    }
                    else
                    {
                        ValidateXml(text, cancellationToken);
                    }
                    return new PatchFormatResult(kind, PatchFormatEvidence.Structure, "Configuration");
                }
            }
            stream.Position = 0;
            switch (kind)
            {
                case PatchFormatKind.Elf:
                    ElfReader.Read(stream, cancellationToken);
                    return new PatchFormatResult(kind, PatchFormatEvidence.Structure, "ElfTables");
                case PatchFormatKind.Sparse:
                    ValidateSparse(stream, cancellationToken);
                    return new PatchFormatResult(kind, PatchFormatEvidence.Structure, "SparseChunks");
                case PatchFormatKind.Vbmeta:
                    AvbMetadataReader.Read(stream, cancellationToken);
                    return new PatchFormatResult(kind, PatchFormatEvidence.Structure, "AvbDescriptors");
                default:
                    cancellationToken.ThrowIfCancellationRequested();
                    return new PatchFormatResult(kind, kind == PatchFormatKind.Unknown ? PatchFormatEvidence.Unknown : PatchFormatEvidence.Signature,
                        kind == PatchFormatKind.Unknown ? "Unknown" : "SignatureOnly");
            }
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or JsonException or XmlException
            or DecoderFallbackException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PatchFormatResult(kind, kind == PatchFormatKind.Unknown ? PatchFormatEvidence.Unknown : PatchFormatEvidence.Signature,
                "RejectedStructure", true);
        }
        finally { stream.Position = originalPosition; }
    }

    private static PatchFormatKind Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("\u007fELF"u8)) { return PatchFormatKind.Elf; }
        if (bytes.StartsWith("ANDROID!"u8)) { return PatchFormatKind.Boot; }
        if (bytes.StartsWith("VNDRBOOT"u8)) { return PatchFormatKind.VendorBoot; }
        if (bytes.StartsWith("AVB0"u8)) { return PatchFormatKind.Vbmeta; }
        if (bytes.StartsWith("CrAU"u8)) { return PatchFormatKind.Payload; }
        if (bytes.StartsWith("dex\n"u8) || bytes.StartsWith("cdex"u8)) { return PatchFormatKind.Dex; }
        if (bytes.StartsWith("PK\u0003\u0004"u8) || bytes.StartsWith("PK\u0005\u0006"u8)) { return PatchFormatKind.Zip; }
        if (bytes.StartsWith("070701"u8) || bytes.StartsWith("070702"u8) || bytes.StartsWith("070707"u8)) { return PatchFormatKind.Cpio; }
        if (bytes.StartsWith("BSDIFF40"u8) || bytes.StartsWith("BSDF2"u8)) { return PatchFormatKind.Bsdiff; }
        if (bytes.StartsWith("RKAF"u8) || bytes.StartsWith("RKFW"u8)) { return PatchFormatKind.Rockchip; }
        if (bytes.StartsWith("OPS\0"u8)) { return PatchFormatKind.Ops; }
        if (bytes.StartsWith("OPPOENCRYPT!"u8)) { return PatchFormatKind.Ozip; }
        if (bytes.StartsWith("NTPI"u8)) { return PatchFormatKind.Ntpi; }
        if (bytes.Length >= 3 && bytes[0] == 0x1f && bytes[1] == 0x8b && bytes[2] == 8) { return PatchFormatKind.Gzip; }
        if (bytes.StartsWith(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0 })) { return PatchFormatKind.Xz; }
        if (bytes.Length >= 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (magic == SparseFormat.SparseHeaderMagic) { return PatchFormatKind.Sparse; }
            if (magic == 0xfd2fb528) { return PatchFormatKind.Zstd; }
            if (magic is 0x184d2204 or 0x184c2102) { return PatchFormatKind.Lz4; }
            if (magic == 0xedfe0dd0) { return PatchFormatKind.Dtb; }
            if (magic == 0x1eabb7d7) { return PatchFormatKind.Dtbo; }
        }
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0x27b51956) { return PatchFormatKind.Amlogic; }
        if (bytes.Length >= 1028)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1024..]);
            if (magic == 0xe0f5e1e2) { return PatchFormatKind.Erofs; }
            if (magic == 0xf2f52010) { return PatchFormatKind.F2fs; }
        }
        if (bytes.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[1080..]) == 0xef53) { return PatchFormatKind.ExtFamily; }
        return PatchFormatKind.Unknown;
    }

    private static PatchFormatKind DetectText(ReadOnlySpan<byte> bytes, out bool whitespace)
    {
        var (encoding, skip) = EncodingFor(bytes);
        // Only decode complete characters in the prefix; the full document is decoded strictly later.
        Span<char> chars = stackalloc char[ProbeSize];
        encoding.GetDecoder().Convert(bytes[skip..], chars, false, out _, out var used, out _);
        var prefix = chars[..used].TrimStart();
        whitespace = prefix.IsEmpty;
        return DetectText(prefix);
    }

    private static PatchFormatKind DetectText(ReadOnlySpan<char> text)
    {
        var prefix = text.TrimStart();
        if (prefix.IsEmpty) { return PatchFormatKind.Unknown; }
        return prefix[0] switch
        {
            '<' => PatchFormatKind.Xml,
            '{' or '[' or '"' or '-' or >= '0' and <= '9' or 't' or 'f' or 'n' => PatchFormatKind.Json,
            _ => PatchFormatKind.Unknown
        };
    }

    private static (Encoding Encoding, int Skip) EncodingFor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { return (new UTF8Encoding(false, true), 3); }
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { return (new UnicodeEncoding(false, false, true), 2); }
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { return (new UnicodeEncoding(true, false, true), 2); }
        return (new UTF8Encoding(false, true), 0);
    }

    private static string ReadText(Stream stream, ReadOnlySpan<byte> header, CancellationToken token)
    {
        if (stream.Length > TextPatch.MaxCharacters * 4L) { throw new InvalidDataException("Configuration exceeds the byte limit."); }
        var (encoding, skip) = EncodingFor(header);
        stream.Position = skip;
        using var reader = new StreamReader(stream, encoding, false, 4096, true);
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (builder.Length + count > TextPatch.MaxCharacters) { throw new InvalidDataException("Configuration exceeds the character limit."); }
            builder.Append(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private static void ValidateXml(string text, CancellationToken token)
    {
        using var source = new StringReader(text);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = TextPatch.MaxCharacters
        });
        var entries = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (++entries > MaxEntries || reader.Depth > 64) { throw new InvalidDataException("XML exceeds the structure limit."); }
        }
    }

    private static void ValidateSparse(Stream stream, CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(buffer);
        var header = SparseHeader.FromBytes(buffer);
        if (!header.IsValid() || header.TotalChunks > MaxEntries || header.FileHeaderSize > stream.Length)
        {
            throw new InvalidDataException("Invalid or excessive sparse header.");
        }
        _ = checked((long)header.TotalBlocks * header.BlockSize);
        stream.Position = header.FileHeaderSize;
        ulong blocks = 0;
        for (var index = 0u; index < header.TotalChunks; index++)
        {
            token.ThrowIfCancellationRequested();
            var start = stream.Position;
            if (header.ChunkHeaderSize > stream.Length - start) { throw new EndOfStreamException(); }
            stream.ReadExactly(buffer[..SparseFormat.ChunkHeaderSize]);
            var chunk = ChunkHeader.FromBytes(buffer);
            _ = checked((long)chunk.ChunkSize * header.BlockSize);
            if (!chunk.IsValid(header.ChunkHeaderSize, header.BlockSize) || chunk.TotalSize > stream.Length - start
                || chunk.ChunkType == (ushort)ChunkType.Crc32 && chunk.ChunkSize != 0)
            {
                throw new InvalidDataException("Invalid sparse chunk range.");
            }
            blocks += chunk.ChunkSize;
            if (blocks > header.TotalBlocks) { throw new InvalidDataException("Sparse chunks exceed the declared block count."); }
            stream.Position = start + chunk.TotalSize;
        }
        if (blocks != header.TotalBlocks || stream.Position != stream.Length)
        {
            throw new InvalidDataException("Sparse image has inconsistent block totals or trailing data.");
        }
    }
}
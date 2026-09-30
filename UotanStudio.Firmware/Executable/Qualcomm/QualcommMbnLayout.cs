using System.Buffers.Binary;

namespace UotanStudio.Firmware.Executable.Qualcomm;

internal readonly record struct QualcommMbnLayout(
    uint Version, uint? ImageId, uint? SoftwareId, int HashOffset, int HashLength,
    int QualcommSignatureOffset, int QualcommSignatureLength, int QualcommCertificateOffset,
    int QualcommCertificateLength, int OemSignatureOffset, int OemSignatureLength,
    int OemCertificateOffset, int OemCertificateLength, int QualcommMetadataOffset,
    int QualcommMetadataLength, int OemMetadataOffset, int OemMetadataLength, uint HashAlgorithm,
    int? QualcommRootSlot, int? OemRootSlot, uint MetadataRootAlgorithm, byte[]? MetadataRootDigest)
{
    internal static QualcommMbnLayout Read(Stream stream, long baseOffset, long available, CancellationToken token)
    {
        if (available < 40)
        {
            throw new ExecutableCorruptedException("Truncated Qualcomm MBN header.");
        }

        Span<byte> header = stackalloc byte[48];
        ReadAt(stream, baseOffset, header[..(int)Math.Min(48, available)], token);
        var version = Word(header, 4);
        if (version is not (3 or 5 or 6 or 7))
        {
            throw new ExecutableUnsupportedFeatureException("Unsupported Qualcomm MBN header version.");
        }

        var headerSize = version == 6 ? 48 : 40;
        if (available < headerSize)
        {
            throw new ExecutableCorruptedException("Truncated Qualcomm MBN header.");
        }

        ulong qtiMetadata = version == 6 ? Word(header, 40) : version == 7 ? Word(header, 12) : 0;
        ulong oemMetadata = version == 6 ? Word(header, 44) : version == 7 ? Word(header, 16) : 0;
        ulong commonMetadata = version == 7 ? Word(header, 8) : 0;
        if (version == 7 && commonMetadata < 24)
        {
            throw new ExecutableCorruptedException("Invalid MBN v7 common metadata length.");
        }

        ulong metadata = qtiMetadata + oemMetadata + commonMetadata;
        ulong hashLength = Word(header, 20);
        if (hashLength == 0)
        {
            throw new ExecutableCorruptedException("Qualcomm MBN hash table is empty.");
        }
        ulong qtiSignature = version == 3 ? 0 : Word(header, version == 7 ? 24 : 8);
        ulong qtiCertificate = version == 3 ? 0 : Word(header, version == 7 ? 28 : 12);
        ulong oemSignature = Word(header, version == 7 ? 32 : 28);
        ulong oemCertificate = Word(header, version == 7 ? 36 : 36);
        ulong required = metadata + hashLength + qtiSignature + qtiCertificate + oemSignature + oemCertificate;
        ulong declared = version == 7 ? required : Word(header, 16);
        if (required > 64UL * 1024 * 1024 || hashLength > 32UL * 1024 * 1024
            || declared < required - metadata
            || (version != 7 && declared > (ulong)available - (uint)headerSize)
            || required > (ulong)available - (uint)headerSize
            || qtiCertificate > 16UL * 1024 * 1024 || oemCertificate > 16UL * 1024 * 1024
            || qtiSignature > 8192 || oemSignature > 8192)
        {
            throw new ExecutableCorruptedException("Qualcomm MBN length or authentication ranges are invalid.");
        }

        var hash = checked((int)((ulong)headerSize + metadata));
        var qtiSig = checked(hash + (int)hashLength);
        var qtiCert = checked(qtiSig + (int)qtiSignature);
        var oemSig = checked(qtiCert + (int)qtiCertificate);
        var oemCert = checked(oemSig + (int)oemSignature);
        uint? softwareId = null;
        uint algorithm = version is 6 or 7 ? 3u : 0u;
        int? qtiRootSlot = null;
        int? oemRootSlot = null;
        uint rootAlgorithm = 0;
        byte[]? rootDigest = null;
        if (version == 7)
        {
            Span<byte> common = stackalloc byte[24];
            ReadAt(stream, baseOffset + headerSize, common, token);
            softwareId = Word(common, 0) == 0 && Word(common, 4) == 0 ? Word(common, 8) : null;
            algorithm = BinaryPrimitives.ReadUInt32LittleEndian(common[16..]);
        }
        if (version == 7)
        {
            if (qtiMetadata >= 16)
            {
                qtiRootSlot = ReadWordAt(stream, baseOffset + headerSize + (long)commonMetadata + 12, token);
            }

            if (oemMetadata >= 224)
            {
                var oem = baseOffset + headerSize + (long)commonMetadata + (long)qtiMetadata;
                oemRootSlot = ReadWordAt(stream, oem + 12, token);
                Span<byte> word = stackalloc byte[4];
                ReadAt(stream, oem + 152, word, token);
                rootAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(word);
                rootDigest = new byte[64];
                ReadAt(stream, oem + 156, rootDigest, token);
                if (rootDigest.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    rootDigest = null;
                }
            }
        }

        var layout = new QualcommMbnLayout(version, version == 7 ? null : Word(header, 0), softwareId, hash, (int)hashLength,
            qtiSig, (int)qtiSignature, qtiCert, (int)qtiCertificate, oemSig, (int)oemSignature,
            oemCert, (int)oemCertificate, checked(headerSize + (int)commonMetadata), (int)qtiMetadata,
            checked(headerSize + (int)commonMetadata + (int)qtiMetadata), (int)oemMetadata, algorithm,
            qtiRootSlot, oemRootSlot, rootAlgorithm, rootDigest);
        if (version == 6)
        {
            var metadataFields = QualcommMetadataReader.Read(stream, baseOffset, layout, token);
            var qti = metadataFields.FirstOrDefault(value => value.Authority == "QTI");
            var oem = metadataFields.FirstOrDefault(value => value.Authority == "OEM");
            if (qti?.RootCertificateSlot > 63 || oem?.RootCertificateSlot > 63)
            {
                throw new ExecutableUnsupportedFeatureException("MBN root-certificate slot exceeds the supported limit.");
            }
            layout = layout with
            {
                SoftwareId = (uint?)oem?.SoftwareId,
                QualcommRootSlot = (int?)qti?.RootCertificateSlot,
                OemRootSlot = (int?)oem?.RootCertificateSlot
            };
        }
        return layout;
    }

    private static int ReadWordAt(Stream stream, long offset, CancellationToken token)
    {
        Span<byte> word = stackalloc byte[4];
        ReadAt(stream, offset, word, token);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(word);
        if (value > 63)
        {
            throw new ExecutableUnsupportedFeatureException("MBN root-certificate slot exceeds the supported limit.");
        }

        return (int)value;
    }

    private static uint Word(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    internal static void ReadAt(Stream stream, long offset, Span<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || offset > stream.Length || data.Length > stream.Length - offset)
        {
            throw new ExecutableCorruptedException("Qualcomm data range exceeds the file.");
        }

        stream.Position = offset;
        stream.ReadExactly(data);
        token.ThrowIfCancellationRequested();
    }
}
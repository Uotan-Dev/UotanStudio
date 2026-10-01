using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>Reads AVB configuration without treating the embedded key as a trust anchor.</summary>
internal static class AvbMetadataReader
{
    private const ulong MaximumMetadataSize = 16 * 1024 * 1024;
    private const int MaximumDescriptors = 4096;

    internal static Dictionary<string, string> Read(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = new Dictionary<string, string>();
        var offset = 0UL;
        var available = (ulong)stream.Length;
        Span<byte> headerBytes = stackalloc byte[AvbVBMetaImageHeader.Size];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);
        if (!headerBytes[..4].SequenceEqual("AVB0"u8))
        {
            Span<byte> footerBytes = stackalloc byte[AvbFooter.Size];
            stream.Position = stream.Length - AvbFooter.Size;
            stream.ReadExactly(footerBytes);
            var footer = AvbFooter.TryLoad(footerBytes) ?? throw new InvalidDataException("No AVB footer or VBMeta header found.");
            Range(footer.VbmetaOffset, footer.VbmetaSize, (ulong)stream.Length - AvbFooter.Size);
            if (footer.OriginalImageSize > footer.VbmetaOffset)
            {
                throw new InvalidDataException("AVB original image overlaps VBMeta.");
            }
            offset = footer.VbmetaOffset;
            available = footer.VbmetaSize;
            fields["FooterVersion"] = $"{footer.VersionMajor}.{footer.VersionMinor}";
            fields["OriginalImageSize"] = MetadataDisplay.Number(footer.OriginalImageSize);
            Range(0, (ulong)headerBytes.Length, available);
            stream.Position = (long)offset;
            stream.ReadExactly(headerBytes);
        }

        var header = AvbVBMetaImageHeader.FromBytes(headerBytes);
        if (!header.IsValid())
        {
            throw new InvalidDataException("Invalid VBMeta header.");
        }
        Range(256, header.AuthenticationDataBlockSize, available);
        var auxiliaryOffset = 256 + header.AuthenticationDataBlockSize;
        Range(auxiliaryOffset, header.AuxiliaryDataBlockSize, available);
        if (header.AuthenticationDataBlockSize > MaximumMetadataSize || header.AuxiliaryDataBlockSize > MaximumMetadataSize)
        {
            throw new InvalidDataException("AVB metadata exceeds the 16 MiB inspection limit per block.");
        }
        fields["VbmetaOffset"] = MetadataDisplay.Number(offset);
        fields["VbmetaSize"] = MetadataDisplay.Number(auxiliaryOffset + header.AuxiliaryDataBlockSize);
        fields["RequiredLibavbVersion"] = $"{header.RequiredLibavbVersionMajor}.{header.RequiredLibavbVersionMinor}";
        fields["AlgorithmType"] = MetadataDisplay.Number(header.AlgorithmType);
        fields["AlgorithmName"] = header.AlgorithmType switch
        {
            0 => "NONE",
            1 => "SHA256_RSA2048",
            2 => "SHA256_RSA4096",
            3 => "SHA256_RSA8192",
            4 => "SHA512_RSA2048",
            5 => "SHA512_RSA4096",
            6 => "SHA512_RSA8192",
            _ => $"0x{header.AlgorithmType:X8}"
        };
        fields["Flags"] = MetadataDisplay.Flags(header.Flags, (1, "HASHTREE_DISABLED"), (2, "VERIFICATION_DISABLED"));
        fields["HashtreeDisabled"] = ((header.Flags & 1) != 0).ToString();
        fields["VerificationDisabled"] = ((header.Flags & 2) != 0).ToString();
        fields["VerificationStatus"] = "NotPerformed";
        fields["RollbackIndex"] = MetadataDisplay.Number(header.RollbackIndex);
        fields["RollbackIndexLocation"] = MetadataDisplay.Number(header.RollbackIndexLocation);
        fields["ReleaseString"] = header.ReleaseString;
        fields["AuthenticationDataBlockSize"] = MetadataDisplay.Number(header.AuthenticationDataBlockSize);
        fields["AuxiliaryDataBlockSize"] = MetadataDisplay.Number(header.AuxiliaryDataBlockSize);
        AddRegion("Hash", 256, header.HashOffset, header.HashSize, header.AuthenticationDataBlockSize);
        AddRegion("Signature", 256, header.SignatureOffset, header.SignatureSize, header.AuthenticationDataBlockSize);
        AddRegion("PublicKey", auxiliaryOffset, header.PublicKeyOffset, header.PublicKeySize, header.AuxiliaryDataBlockSize);
        AddRegion("PublicKeyMetadata", auxiliaryOffset, header.PublicKeyMetadataOffset, header.PublicKeyMetadataSize, header.AuxiliaryDataBlockSize);
        fields["DescriptorsOffset"] = MetadataDisplay.Number(header.DescriptorsOffset);
        fields["DescriptorsSize"] = MetadataDisplay.Number(header.DescriptorsSize);
        Range(header.DescriptorsOffset, header.DescriptorsSize, header.AuxiliaryDataBlockSize);
        var position = 0UL;
        var index = 0;
        Span<byte> descriptorHeader = stackalloc byte[16];
        while (position < header.DescriptorsSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++index > MaximumDescriptors)
            {
                throw new InvalidDataException("AVB descriptor count exceeds the 4096 inspection limit.");
            }
            Range(position, 16, header.DescriptorsSize);
            stream.Position = checked((long)(offset + auxiliaryOffset + header.DescriptorsOffset + position));
            stream.ReadExactly(descriptorHeader);
            var tag = U64(descriptorHeader, 0);
            var length = U64(descriptorHeader, 8);
            Range(position + 16, length, header.DescriptorsSize);
            if (length % 8 != 0)
            {
                throw new InvalidDataException("AVB descriptor is not aligned to 8 bytes.");
            }
            var body = new byte[(int)length];
            stream.ReadExactly(body);
            ReadDescriptor(fields, $"Descriptor[{index}]", tag, body);
            position += 16 + length;
        }
        fields["DescriptorCount"] = MetadataDisplay.Number(index);
        return fields;

        void AddRegion(string name, ulong blockOffset, ulong relativeOffset, ulong length, ulong blockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Range(relativeOffset, length, blockSize);
            fields[name + "Offset"] = MetadataDisplay.Number(relativeOffset);
            fields[name + "Size"] = MetadataDisplay.Number(length);
            stream.Position = checked((long)(offset + blockOffset + relativeOffset));
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            fields[name] = Convert.ToHexString(bytes);
            if (name == "PublicKey" && bytes.Length > 0)
            {
                fields["PublicKeySha256"] = Convert.ToHexString(SHA256.HashData(bytes));
                if (bytes.Length >= 8)
                {
                    fields["PublicKeyBits"] = MetadataDisplay.Number(U32(bytes, 0));
                }
            }
        }
    }

    private static void ReadDescriptor(Dictionary<string, string> fields, string prefix, ulong tag, ReadOnlySpan<byte> body)
    {
        fields[prefix + ".Tag"] = MetadataDisplay.Number(tag);
        fields[prefix + ".Size"] = MetadataDisplay.Number(body.Length + 16);
        fields[prefix + ".Type"] = tag switch
        {
            0 => "Property",
            1 => "Hashtree",
            2 => "Hash",
            3 => "KernelCmdline",
            4 => "ChainPartition",
            _ => "Unknown"
        };
        switch (tag)
        {
            case 0:
                Range(0, 16, (ulong)body.Length);
                var keyLength = U64(body, 0);
                var valueLength = U64(body, 8);
                var key = Slice(body, 16, keyLength);
                var value = Slice(body, 17 + keyLength, valueLength);
                if (Slice(body, 16 + keyLength, 1)[0] != 0 || Slice(body, 17 + keyLength + valueLength, 1)[0] != 0)
                {
                    throw new InvalidDataException("AVB property terminator is missing.");
                }
                fields[prefix + ".Key"] = Encoding.UTF8.GetString(key);
                fields[prefix + ".Value"] = Encoding.UTF8.GetString(value);
                fields[prefix + ".ValueHex"] = Convert.ToHexString(value);
                break;
            case 1:
            case 2:
                var tree = tag == 1;
                var fixedSize = tree ? 164UL : 116UL;
                Range(0, fixedSize, (ulong)body.Length);
                var lengths = tree ? 88 : 40;
                var nameLength = U32(body, lengths);
                var saltLength = U32(body, lengths + 4);
                var digestLength = U32(body, lengths + 8);
                fields[prefix + ".ImageSize"] = MetadataDisplay.Number(U64(body, tree ? 4 : 0));
                fields[prefix + ".HashAlgorithm"] = Encoding.ASCII.GetString(body.Slice(tree ? 56 : 8, 32)).TrimEnd('\0');
                fields[prefix + ".PartitionName"] = Encoding.UTF8.GetString(Slice(body, fixedSize, nameLength));
                fields[prefix + ".Salt"] = Convert.ToHexString(Slice(body, fixedSize + nameLength, saltLength));
                fields[prefix + (tree ? ".RootDigest" : ".Digest")] = Convert.ToHexString(Slice(body, fixedSize + nameLength + saltLength, digestLength));
                var flags = U32(body, lengths + 12);
                fields[prefix + ".Flags"] = tree
                    ? MetadataDisplay.Flags(flags, (1, "DO_NOT_USE_AB"), (2, "CHECK_AT_MOST_ONCE"))
                    : MetadataDisplay.Flags(flags, (1, "DO_NOT_USE_AB"));
                if (tree)
                {
                    fields[prefix + ".DmVerityVersion"] = MetadataDisplay.Number(U32(body, 0));
                    fields[prefix + ".TreeOffset"] = MetadataDisplay.Number(U64(body, 12));
                    fields[prefix + ".TreeSize"] = MetadataDisplay.Number(U64(body, 20));
                    fields[prefix + ".DataBlockSize"] = MetadataDisplay.Number(U32(body, 28));
                    fields[prefix + ".HashBlockSize"] = MetadataDisplay.Number(U32(body, 32));
                    fields[prefix + ".FecNumRoots"] = MetadataDisplay.Number(U32(body, 36));
                    fields[prefix + ".FecOffset"] = MetadataDisplay.Number(U64(body, 40));
                    fields[prefix + ".FecSize"] = MetadataDisplay.Number(U64(body, 48));
                }
                break;
            case 3:
                Range(0, 8, (ulong)body.Length);
                fields[prefix + ".Flags"] = MetadataDisplay.Flags(U32(body, 0), (1, "USE_ONLY_IF_HASHTREE_NOT_DISABLED"), (2, "USE_ONLY_IF_HASHTREE_DISABLED"));
                fields[prefix + ".KernelCmdline"] = Encoding.UTF8.GetString(Slice(body, 8, U32(body, 4)));
                break;
            case 4:
                Range(0, 76, (ulong)body.Length);
                fields[prefix + ".RollbackIndexLocation"] = MetadataDisplay.Number(U32(body, 0));
                var partitionLength = U32(body, 4);
                fields[prefix + ".PartitionName"] = Encoding.UTF8.GetString(Slice(body, 76, partitionLength));
                var publicKey = Slice(body, 76UL + partitionLength, U32(body, 8));
                fields[prefix + ".PublicKey"] = Convert.ToHexString(publicKey);
                fields[prefix + ".PublicKeySha256"] = Convert.ToHexString(SHA256.HashData(publicKey));
                fields[prefix + ".Flags"] = MetadataDisplay.Flags(U32(body, 12), (1, "DO_NOT_USE_AB"));
                break;
            default:
                fields[prefix + ".RawData"] = Convert.ToHexString(body);
                break;
        }
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, ulong offset, ulong length)
    {
        Range(offset, length, (ulong)bytes.Length);
        return bytes.Slice((int)offset, (int)length);
    }

    private static void Range(ulong offset, ulong length, ulong available)
    {
        if (offset > available || length > available - offset)
        {
            throw new InvalidDataException("AVB metadata range exceeds its containing block.");
        }
    }
}
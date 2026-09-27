using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

internal static class QualcommMetadataReader
{
    private const string OrganizationalUnitOid = "2.5.4.11";
    private const int MaximumAttributeFields = 64;
    private const int MaximumAttributeValueLength = 4096;

    private static readonly (string Token, HashAlgorithmName Algorithm)[] HashTokens =
    [
        ("SHA384", HashAlgorithmName.SHA384),
        ("SHA512", HashAlgorithmName.SHA512),
        ("SHA256", HashAlgorithmName.SHA256),
        ("SHA1", HashAlgorithmName.SHA1)
    ];

    internal static IReadOnlyList<QualcommImageRegion> Regions(long offset, QualcommMbnLayout layout)
    {
        var headerSize = layout.Version == 6 ? 48 : 40;
        var regions = new List<QualcommImageRegion> { new("MbnHeader", offset, headerSize) };
        Add("CommonMetadata", headerSize, layout.QualcommMetadataOffset - headerSize);
        Add("QtiMetadata", layout.QualcommMetadataOffset, layout.QualcommMetadataLength);
        Add("OemMetadata", layout.OemMetadataOffset, layout.OemMetadataLength);
        Add("HashTable", layout.HashOffset, layout.HashLength);
        Add("QtiSignature", layout.QualcommSignatureOffset, layout.QualcommSignatureLength);
        Add("QtiCertificates", layout.QualcommCertificateOffset, layout.QualcommCertificateLength);
        Add("OemSignature", layout.OemSignatureOffset, layout.OemSignatureLength);
        Add("OemCertificates", layout.OemCertificateOffset, layout.OemCertificateLength);
        return regions.AsReadOnly();

        void Add(string name, int relative, int length)
        {
            if (length > 0)
            {
                regions.Add(new(name, checked(offset + relative), length));
            }
        }
    }

    internal static IReadOnlyList<QualcommSigningMetadata> Read(
        Stream stream, long offset, QualcommMbnLayout layout, CancellationToken token)
    {
        var result = new List<QualcommSigningMetadata>();
        if (layout.Version == 7)
        {
            Span<byte> common = stackalloc byte[24];
            QualcommMbnLayout.ReadAt(stream, offset + 40, common, token);
            var metadata = new QualcommSigningMetadata
            {
                Authority = "Common",
                FileOffset = offset + 40,
                MajorVersion = Word(common, 0),
                MinorVersion = Word(common, 4)
            };
            if (metadata.MajorVersion == 0 && metadata.MinorVersion == 0)
            {
                metadata = metadata with { Layout = "CommonV7", SoftwareId = Word(common, 8), SecondarySoftwareId = Word(common, 12) };
            }
            result.Add(metadata);
        }
        Add("QTI", layout.QualcommMetadataOffset, layout.QualcommMetadataLength);
        Add("OEM", layout.OemMetadataOffset, layout.OemMetadataLength);
        return result.AsReadOnly();

        void Add(string authority, int relative, int length)
        {
            if (layout.Version is not (6 or 7) || length < 8)
            {
                return;
            }

            Span<byte> data = stackalloc byte[224];
            var size = Math.Min(length, 224);
            QualcommMbnLayout.ReadAt(stream, offset + relative, data[..size], token);
            var metadata = new QualcommSigningMetadata
            {
                Authority = authority,
                FileOffset = offset + relative,
                MajorVersion = Word(data, 0),
                MinorVersion = Word(data, 4)
            };
            if (size == length && data[..size].IndexOfAnyExcept((byte)0) < 0)
            {
                result.Add(metadata with { Layout = "ZeroFilledMetadata" });
                return;
            }
            if (metadata.MajorVersion != 0 || metadata.MinorVersion != 0)
            {
                result.Add(metadata);
                return;
            }
            if (layout.Version == 6 && length == 120)
            {
                metadata = metadata with
                {
                    Layout = "PackedV6",
                    SoftwareId = Word(data, 8),
                    HardwareId = Word(data, 12),
                    OemId = Word(data, 16),
                    ModelId = Word(data, 20),
                    ApplicationId = Word(data, 24),
                    Flags = Word(data, 28),
                    SocVersions = Words(data, 32, 12),
                    SerialNumbers = Serials(data, 80, 4),
                    RootCertificateSlot = Word(data, 112),
                    AntiRollbackVersion = Word(data, 116)
                };
            }
            else if (layout.Version == 6 && length == 168)
            {
                metadata = ReadTaggedV6(data[..size], metadata);
            }
            else if (layout.Version == 7 && length is 16 or 224)
            {
                metadata = metadata with { Layout = "MetadataV7", AntiRollbackVersion = Word(data, 8), RootCertificateSlot = Word(data, 12) };
                if (size >= 224)
                {
                    metadata = metadata with
                    {
                        SocVersions = Words(data, 16, 12),
                        SocFeatureId = Word(data, 64),
                        JtagId = Word(data, 68),
                        SerialNumbers = Serials(data, 72, 8),
                        OemId = Word(data, 136),
                        ModelId = Word(data, 140),
                        SocLifecycleState = Word(data, 144),
                        OemLifecycleState = Word(data, 148),
                        RootHashAlgorithm = Word(data, 152),
                        RootHash = Convert.ToHexString(data.Slice(156, 64)),
                        Flags = Word(data, 220)
                    };
                }
            }
            result.Add(metadata);
        }
    }

    private static QualcommSigningMetadata ReadTaggedV6(ReadOnlySpan<byte> data, QualcommSigningMetadata metadata)
    {
        var values = new Dictionary<uint, uint[]>();
        var cursor = 8;
        while (cursor < data.Length)
        {
            var id = Word(data, cursor);
            cursor += 4;
            var count = id switch { 8 => 12, 9 => 8, >= 1 and <= 11 => 1, _ => 0 };
            if (count == 0 || count * 4 > data.Length - cursor || !values.TryAdd(id, Words(data, cursor, count)))
            {
                return metadata;
            }
            cursor += count * 4;
        }
        if (values.Count != 11)
        {
            return metadata;
        }
        return metadata with
        {
            Layout = "TaggedV6",
            SoftwareId = values[1][0],
            HardwareId = values[2][0],
            OemId = values[3][0],
            SoftwareSize = values[4][0],
            ModelId = values[5][0],
            ApplicationId = values[6][0],
            Flags = values[7][0],
            SocVersions = values[8],
            SerialNumbers = values[9].Select(value => (ulong)value).ToArray(),
            RootCertificateSlot = values[10][0],
            AntiRollbackVersion = values[11][0]
        };
    }

    internal static IReadOnlyList<QualcommCertificateAttribute> ReadAttributes(byte[] subject)
    {
        var fields = new List<QualcommCertificateAttribute>();
        if (!TryEnumerateOrganizationalUnits(subject, null, value =>
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Length == 2 && parts[0].All(char.IsAsciiDigit))
            {
                fields.Add(new(parts[^1], string.Join(' ', parts[1..^1])));
            }

            return fields.Count < MaximumAttributeFields;
        }))
        {
            return [];
        }

        return fields.AsReadOnly();
    }

    /// <summary>
    /// Resolves the digest algorithms declared by every organizational-unit value of a subject name.
    /// A value declares an algorithm when it contains one of the tokens SHA384, SHA512, SHA256 or SHA1,
    /// matched case-insensitively in that priority order and delimited so that a longer token such as
    /// SHA1024 declares nothing. Unlike <see cref="ReadAttributes"/> no digit-prefix shape is required,
    /// because Qualcomm hash declarations appear as bare units. The declaration is advisory: it only
    /// orders candidate digests and never authenticates anything on its own.
    /// <para>解析主题名中所有 organizational unit 值声明的摘要算法。当某个值包含 SHA384、SHA512、
    /// SHA256、SHA1 之一时即视为声明该算法，按此优先级大小写不敏感匹配，并要求分隔，因此
    /// SHA1024 之类的更长标记不构成声明。与 <see cref="ReadAttributes"/> 不同，此处不要求数字前缀形状，
    /// 因为 Qualcomm 的哈希声明通常是裸单位。该声明仅为参考信息：只用于排列候选摘要顺序，
    /// 本身不构成任何认证结果。</para>
    /// </summary>
    /// <param name="subject">DER-encoded X.500 subject name. <para>DER 编码的 X.500 主题名。</para></param>
    /// <returns>The distinct declarations, with <see cref="QualcommOuHashDeclaration.Algorithm"/> set only
    /// when exactly one algorithm was declared and the ambiguity flag cleared.
    /// <para>去重后的声明集合；仅当恰好声明了一种算法且不存在歧义时
    /// <see cref="QualcommOuHashDeclaration.Algorithm"/> 才有值。</para></returns>
    internal static QualcommOuHashDeclaration ReadOrganizationalUnitHashDeclaration(byte[] subject)
    {
        var declarations = new List<HashAlgorithmName>(4);
        TryEnumerateOrganizationalUnits(subject, MaximumAttributeFields, value =>
        {
            if (MatchHashToken(value) is { } match && !declarations.Contains(match))
            {
                declarations.Add(match);
            }

            return true;
        });
        return declarations.Count == 1
            ? new(declarations[0], false, [])
            : new(null, declarations.Count > 1, declarations.Select(value => value.Name!).ToArray());
    }

    private static bool TryEnumerateOrganizationalUnits(byte[] subject, int? maximumValues,
        Func<string, bool> handle)
    {
        try
        {
            var reader = new AsnReader(subject, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var values = 0;
            while (sequence.HasData && (maximumValues is null || values < maximumValues))
            {
                var set = sequence.ReadSetOf(skipSortOrderValidation: true);
                while (set.HasData && (maximumValues is null || values < maximumValues))
                {
                    var attribute = set.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    var tag = attribute.PeekTag();
                    if (oid != OrganizationalUnitOid || tag.TagClass != TagClass.Universal
                        || tag.TagValue is not (12 or 19 or 20 or 22 or 28 or 30))
                    {
                        attribute.ReadEncodedValue();
                        continue;
                    }

                    var value = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                    if (value.Length > MaximumAttributeValueLength)
                    {
                        continue;
                    }

                    values++;
                    if (!handle(value))
                    {
                        return true;
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            return false;
        }

        return true;
    }

    private static HashAlgorithmName? MatchHashToken(string value)
    {
        foreach (var (token, algorithm) in HashTokens)
        {
            for (var start = value.IndexOf(token, StringComparison.OrdinalIgnoreCase); start >= 0;
                 start = value.IndexOf(token, start + token.Length, StringComparison.OrdinalIgnoreCase))
            {
                var end = start + token.Length;
                var before = start == 0 || !char.IsAsciiLetterOrDigit(value[start - 1]);
                var after = end == value.Length || !char.IsAsciiLetterOrDigit(value[end]);
                if (before && after)
                {
                    return algorithm;
                }
            }
        }

        return null;
    }

    internal static QualcommSigningMetadata FromCertificate(QualcommCertificateInfo certificate)
    {
        var sw = Single("SW_ID");
        return new()
        {
            Authority = certificate.Authority,
            FromCertificate = true,
            Layout = "CertificateOu",
            SoftwareId = sw,
            HardwareId = Single("HW_ID"),
            OemId = Small("OEM_ID"),
            ModelId = Small("MODEL_ID"),
            ApplicationId = Small("APP_ID"),
            SoftwareSize = Small("SW_SIZE"),
            AntiRollbackVersion = sw.HasValue ? (uint)(sw.Value >> 32) : null,
            RootCertificateSlot = Small("MRC_INDEX"),
            SocVersions = Values("SOC_VERS").Where(v => v <= uint.MaxValue).Select(v => (uint)v).ToArray(),
            SerialNumbers = Values("SN")
        };

        ulong? Single(string name)
        {
            var fields = certificate.Attributes.Where(item => item.Name == name).ToArray();
            return fields.Length == 1 && ulong.TryParse(fields[0].Value, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        uint? Small(string name) => Single(name) is { } value && value <= uint.MaxValue ? (uint)value : null;

        IReadOnlyList<ulong> Values(string name)
        {
            var values = new List<ulong>();
            foreach (var field in certificate.Attributes.Where(item => item.Name == name))
            {
                foreach (var part in field.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (values.Count == 64)
                    {
                        return values.AsReadOnly();
                    }
                    if (ulong.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                    {
                        values.Add(value);
                    }
                }
            }
            return values.AsReadOnly();
        }
    }

    private static uint Word(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static uint[] Words(ReadOnlySpan<byte> data, int offset, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = Word(data, offset + i * 4);
        }
        return values;
    }

    private static ulong[] Serials(ReadOnlySpan<byte> data, int offset, int count)
    {
        var values = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + i * 8)..]);
        }
        return values;
    }
}

/// <summary>Carries the digest algorithms declared by certificate organizational units.
/// The declaration orders verification candidates only; it is never treated as authentication evidence.
/// <para>承载证书 organizational unit 声明的摘要算法。该声明仅用于排列验证候选顺序，
/// 从不作为认证证据。</para></summary>
/// <param name="Algorithm">The single declared algorithm, or <see langword="null"/> when nothing was
/// declared or the declarations conflict. <para>唯一声明的算法；未声明或声明冲突时为空。</para></param>
/// <param name="IsConflicting"><see langword="true"/> when two or more distinct algorithms were declared.
/// <para>当声明了两种及以上不同算法时为 <see langword="true"/>。</para></param>
/// <param name="Names">Distinct declared algorithm names in discovery order, bounded by the subject-name
/// field limit. <para>按发现顺序排列的去重算法名称，数量受主题名字段上限约束。</para></param>
internal readonly record struct QualcommOuHashDeclaration(
    HashAlgorithmName? Algorithm,
    bool IsConflicting,
    IReadOnlyList<string> Names);
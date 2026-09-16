using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>One decoded JKS entry: a private-key entry (tag 1) or a trusted-certificate entry (tag 2).</summary>
/// <para>单个解析出的 JKS 条目：私钥条目（tag 1）或受信证书条目（tag 2）。</para>
public sealed record JksEntry(int Tag, string Alias, DateTimeOffset Creation, byte[]? ProtectedPrivateKey, IReadOnlyList<byte[]> Certificates);

/// <summary>
/// Reads back JKS v1 keystores (magic 0xFEEDFEED) with the upstream integrity digest; used to
/// verify generated keystores. Supports both private-key and trusted-certificate entries.
/// <para>按上游完整性摘要读取 JKS v1 密钥库（magic 0xFEEDFEED）；用于校验生成的密钥库。支持私钥条目与受信证书条目。</para>
/// </summary>
public static class JksKeyStoreReader
{
    /// <summary>Parses all entries and verifies the trailing integrity digest.</summary>
    /// <para>解析全部条目并校验尾部完整性摘要。</para>
    public static IReadOnlyList<JksEntry> Read(byte[] data, string password)
    {
        var offset = 0;
        if (ReadInt32(data, ref offset) != unchecked((int)0xFEEDFEED)) throw new InvalidDataException("不是 JKS 密钥库（magic 不匹配）。");
        var version = ReadInt32(data, ref offset);
        if (version is not (1 or 2)) throw new InvalidDataException($"不支持的 JKS 版本：{version}。");
        var count = ReadInt32(data, ref offset);
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("JKS 条目数超出范围。");

        var digest = IntegrityDigest(password);
        var bodyStart = offset;
        var entries = new List<JksEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var tag = ReadInt32(data, ref offset);
            var alias = ReadModifiedUtf(data, ref offset);
            var creation = DateTimeOffset.UnixEpoch.AddMilliseconds(ReadInt64(data, ref offset));
            switch (tag)
            {
                case 1:
                    var protectedKey = ReadBounded(data, ref offset);
                    var certCount = ReadInt32(data, ref offset);
                    if (certCount is < 0 or > 16) throw new InvalidDataException("JKS 证书链长度超出范围。");
                    var chain = new List<byte[]>(certCount);
                    for (var c = 0; c < certCount; c++)
                    {
                        if (version == 2) _ = ReadModifiedUtf(data, ref offset);
                        chain.Add(ReadBounded(data, ref offset));
                    }
                    entries.Add(new JksEntry(tag, alias, creation, protectedKey, chain));
                    break;
                case 2:
                    if (version == 2) _ = ReadModifiedUtf(data, ref offset);
                    var certificate = ReadBounded(data, ref offset);
                    entries.Add(new JksEntry(tag, alias, creation, null, [certificate]));
                    break;
                default:
                    throw new InvalidDataException($"未识别的 JKS 条目类型：{tag}。");
            }
        }

        digest.AppendData(data, bodyStart, offset - bodyStart);
        var computed = digest.GetHashAndReset();
        if (offset + computed.Length != data.Length) throw new InvalidDataException("JKS 尾部长度不符，密钥库已损坏。");
        for (var i = 0; i < computed.Length; i++)
        {
            if (computed[i] != data[offset + i]) throw new InvalidDataException("密码错误或密钥库已被篡改。");
        }
        return entries;
    }

    /// <summary>
    /// Recovers the plaintext PKCS#8 private key from the KeyProtector blob stored in a tag-1
    /// entry (salt || XOR keystream ciphertext || SHA-1 integrity digest), mirroring upstream
    /// KeyProtector.recover.
    /// <para>从 tag-1 条目存储的 KeyProtector 数据块（盐 || XOR 密钥流密文 || SHA-1 完整性摘要）
    /// 恢复明文 PKCS#8 私钥，对应上游 KeyProtector.recover。</para>
    /// </summary>
    /// <param name="protectedKey">The <see cref="JksEntry.ProtectedPrivateKey"/> blob. <para>JksEntry.ProtectedPrivateKey 数据块。</para></param>
    /// <param name="password">Keystore password used when the entry was written. <para>写入条目时使用的密钥库密码。</para></param>
    /// <returns>The plaintext PKCS#8 DER key. <para>明文 PKCS#8 DER 私钥。</para></returns>
    public static byte[] Unprotect(byte[] protectedKey, string password)
    {
        const int saltLength = 20;
        const int digestLength = 20;
        ArgumentNullException.ThrowIfNull(protectedKey);
        if (protectedKey.Length < saltLength + digestLength + 1)
            throw new InvalidDataException("JKS 受保护私钥长度不合法。");
        var keyLength = protectedKey.Length - saltLength - digestLength;
        var passwordBytes = EncodePassword(password);
        try
        {
            var numRounds = (keyLength + digestLength - 1) / digestLength;
            var xorKey = new byte[keyLength];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var digest = protectedKey.AsSpan(0, saltLength).ToArray();
            for (var round = 0; round < numRounds; round++)
            {
                sha.AppendData(passwordBytes);
                sha.AppendData(digest);
                digest = sha.GetHashAndReset();
                Array.Copy(digest, 0, xorKey, round * digestLength, Math.Min(digestLength, xorKey.Length - (round * digestLength)));
            }

            var plainKey = new byte[keyLength];
            for (var i = 0; i < keyLength; i++) plainKey[i] = (byte)(protectedKey[saltLength + i] ^ xorKey[i]);
            sha.AppendData(passwordBytes);
            sha.AppendData(plainKey);
            var computed = sha.GetHashAndReset();
            if (!computed.AsSpan().SequenceEqual(protectedKey.AsSpan(saltLength + keyLength, digestLength)))
            {
                CryptographicOperations.ZeroMemory(plainKey);
                throw new InvalidDataException("密码错误或私钥条目已被篡改。");
            }
            return plainKey;
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    /// <summary>The upstream pre-keyed digest: SHA1(passwordBytes || "Mighty Aphrodite").</summary>
    /// <para>上游 keyed 摘要：SHA1(passwordBytes || "Mighty Aphrodite")。</para>
    internal static IncrementalHash IntegrityDigest(string password)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var passwordBytes = EncodePassword(password);
        hash.AppendData(passwordBytes);
        CryptographicOperations.ZeroMemory(passwordBytes);
        hash.AppendData("Mighty Aphrodite"u8);
        return hash;
    }

    /// <summary>Upstream passwordToBytes: every char as a big-endian 16-bit pair (high byte first).</summary>
    /// <para>上游 passwordToBytes：每个字符按大端 16 位写出（高字节在前）。</para>
    internal static byte[] EncodePassword(string password)
    {
        var bytes = new byte[checked(password.Length * 2)];
        for (var i = 0; i < password.Length; i++)
        {
            bytes[i * 2] = (byte)(password[i] >> 8);
            bytes[(i * 2) + 1] = (byte)password[i];
        }
        return bytes;
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        const int size = 4;
        if (offset > data.Length - size) throw new EndOfStreamException("JKS 数据提前结束。");
        var value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        offset += size;
        return value;
    }

    private static long ReadInt64(byte[] data, ref int offset)
    {
        const int size = 8;
        if (offset > data.Length - size) throw new EndOfStreamException("JKS 数据提前结束。");
        long value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | data[offset + i];
        offset += size;
        return value;
    }

    // Reads a 16-bit big-endian length used by Java DataInputStream.readUTF modified UTF-8 framing.
    // 读取 16 位大端长度，即 Java DataInputStream.readUTF 修改版 UTF-8 框架。
    private static int ReadInt16(byte[] data, ref int offset)
    {
        const int size = 2;
        if (offset > data.Length - size) throw new EndOfStreamException("JKS 数据提前结束。");
        var value = (data[offset] << 8) | data[offset + 1];
        offset += size;
        return value;
    }

    private static byte[] ReadBounded(byte[] data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);
        if (length < 0 || length > data.Length - offset) throw new InvalidDataException("JKS 块长度越界。");
        var bytes = new byte[length];
        Array.Copy(data, offset, bytes, 0, length);
        offset += length;
        return bytes;
    }

    /// <summary>Decodes Java modified UTF-8 with a 2-byte length prefix.</summary>
    /// <para>解码带 2 字节长度前缀的 Java 修改版 UTF-8。</para>
    private static string ReadModifiedUtf(byte[] data, ref int offset)
    {
        var length = ReadInt16(data, ref offset);
        if (length > data.Length - offset) throw new InvalidDataException("JKS 字符串长度越界。");
        var chars = new List<char>(length);
        for (var i = 0; i < length;)
        {
            var a = data[offset + i];
            if ((a & 0x80) == 0)
            {
                chars.Add((char)a);
                i++;
            }
            else if ((a & 0xE0) == 0xC0)
            {
                if (i + 1 >= length) throw new InvalidDataException("JKS 字符串截断。");
                chars.Add((char)(((a & 0x1F) << 6) | (data[offset + i + 1] & 0x3F)));
                i += 2;
            }
            else if ((a & 0xF0) == 0xE0)
            {
                if (i + 2 >= length) throw new InvalidDataException("JKS 字符串截断。");
                chars.Add((char)(((a & 0x0F) << 12) | ((data[offset + i + 1] & 0x3F) << 6) | (data[offset + i + 2] & 0x3F)));
                i += 3;
            }
            else throw new InvalidDataException("JKS 字符串编码非法。");
        }
        offset += length;
        return new string(chars.ToArray());
    }
}

/// <summary>
/// Writes JKS v1 keystores fully compatible with the upstream JavaKeyStore format: big-endian
/// framing, modified UTF-8 aliases, KeyProtector-protected PKCS#8 private keys and the trailing
/// "Mighty Aphrodite" SHA-1 keyed digest. AOT-safe and reflection-free.
/// <para>写出与上游 JavaKeyStore 完全兼容的 JKS v1 密钥库：大端框架、修改版 UTF-8 别名、
/// KeyProtector 保护的 PKCS#8 私钥与尾部 "Mighty Aphrodite" SHA-1 keyed 摘要。AOT 安全、零反射。</para>
/// </summary>
public static class JksKeyStoreWriter
{
    private const int Magic = unchecked((int)0xFEEDFEED);
    private const int Version = 1;
    private const int PrivateKeyTag = 1;
    private const int SaltLength = 20;
    private const int DigestLength = 20;

    /// <summary>
    /// Builds a one-entry JKS keystore holding the private key (protected by the KeyProtector
    /// digest stream) with the certificate chain (leaf first).
    /// <para>构建含单个条目的 JKS 密钥库：私钥经 KeyProtector 摘要流保护，证书链（叶子在前）随条目存储。</para>
    /// </summary>
    /// <param name="alias">Non-empty entry alias. <para>非空的条目别名。</para></param>
    /// <param name="pkcs8PrivateKey">Plaintext PKCS#8 DER private key. <para>明文 PKCS#8 DER 私钥。</para></param>
    /// <param name="certificateChain">Certificate chain DER, leaf first. <para>证书链 DER，叶子在前。</para></param>
    /// <param name="password">Keystore password (non-empty). <para>密钥库密码（非空）。</para></param>
    /// <param name="creation">Entry creation stamp (defaults to now). <para>条目创建时间（默认当前）。</para></param>
    /// <returns>The complete keystore bytes. <para>完整密钥库字节。</para></returns>
    public static byte[] Build(string alias, byte[] pkcs8PrivateKey, IReadOnlyList<byte[]> certificateChain, string password, DateTimeOffset? creation = null)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 255) throw new ArgumentException("别名须为 1–255 个字符。");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("必须提供密钥库密码。");
        if (pkcs8PrivateKey.Length == 0) throw new ArgumentException("私钥内容为空。");
        if (certificateChain.Count == 0) throw new ArgumentException("证书链不能为空。");
        foreach (var certificate in certificateChain)
        {
            if (certificate.Length == 0 || certificate.Length > 1 << 22) throw new ArgumentException("证书大小超出范围。");
        }

        var protectedKey = Protect(pkcs8PrivateKey, password);
        try
        {
            var creationMillis = (creation ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            var aliasBytes = EncodeModifiedUtf(alias);

            using var body = new MemoryStream();
            var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.WriteBEInt32(1);
            using (var digest = JksKeyStoreReader.IntegrityDigest(password))
            {
                void WriteHashedInt32(int value)
                {
                    writer.WriteBEInt32(value);
                    digest.AppendData(WriteBEBytes(new byte[4], value));
                }
                void WriteHashedInt16(int value)
                {
                    writer.WriteBEInt16(value);
                    Span<byte> bytes = [(byte)(value >> 8), (byte)value];
                    digest.AppendData(bytes);
                }
                void WriteHashedInt64(long value)
                {
                    writer.WriteBEInt64(value);
                    Span<byte> bytes = stackalloc byte[8];
                    for (var i = 0; i < 8; i++) bytes[i] = (byte)(value >> ((7 - i) * 8));
                    digest.AppendData(bytes);
                }
                void WriteHashed(ReadOnlySpan<byte> bytes)
                {
                    writer.Write(bytes);
                    digest.AppendData(bytes);
                }

                WriteHashedInt32(PrivateKeyTag);
                WriteHashedInt16(aliasBytes.Length);
                WriteHashed(aliasBytes);
                WriteHashedInt64(creationMillis);
                WriteHashedInt32(protectedKey.Length);
                WriteHashed(protectedKey);
                WriteHashedInt32(certificateChain.Count);
                foreach (var certificate in certificateChain)
                {
                    WriteHashedInt32(certificate.Length);
                    WriteHashed(certificate);
                }

                var trailer = digest.GetHashAndReset();
                writer.Write(trailer);
            }

            using var output = new MemoryStream();
            using (var outputWriter = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                outputWriter.WriteBEInt32(Magic);
                outputWriter.WriteBEInt32(Version);
                outputWriter.Write(body.ToArray());
            }
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
    }

    /// <summary>KeyProtector.protect: SHA1 XOR keystream with random salt, trailing digest(password || plainKey).</summary>
    /// <para>KeyProtector.protect：SHA1 XOR 密钥流加随机盐，尾部附加 digest(password || plainKey)。</para>
    private static byte[] Protect(byte[] plainKey, string password)
    {
        var passwordBytes = JksKeyStoreReader.EncodePassword(password);
        try
        {
            var numRounds = (plainKey.Length + DigestLength - 1) / DigestLength;
            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var xorKey = new byte[plainKey.Length];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var digest = salt;
            for (var round = 0; round < numRounds; round++)
            {
                sha.AppendData(passwordBytes);
                sha.AppendData(digest);
                digest = sha.GetHashAndReset();
                var copyLength = Math.Min(DigestLength, xorKey.Length - (round * DigestLength));
                Array.Copy(digest, 0, xorKey, round * DigestLength, copyLength);
            }

            var protectedKey = new byte[SaltLength + plainKey.Length + DigestLength];
            Array.Copy(salt, protectedKey, SaltLength);
            for (var i = 0; i < plainKey.Length; i++) protectedKey[SaltLength + i] = (byte)(plainKey[i] ^ xorKey[i]);
            sha.AppendData(passwordBytes);
            sha.AppendData(plainKey);
            var check = sha.GetHashAndReset();
            Array.Copy(check, 0, protectedKey, SaltLength + plainKey.Length, DigestLength);
            return protectedKey;
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static byte[] WriteBEBytes(byte[] target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
        return target;
    }

    private static void WriteBEInt32(this BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(value >> 24);
        bytes[1] = (byte)(value >> 16);
        bytes[2] = (byte)(value >> 8);
        bytes[3] = (byte)value;
        writer.Write(bytes);
    }

    private static void WriteBEInt16(this BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        bytes[0] = (byte)(value >> 8);
        bytes[1] = (byte)value;
        writer.Write(bytes);
    }

    private static void WriteBEInt64(this BinaryWriter writer, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < 8; i++) bytes[i] = (byte)(value >> ((7 - i) * 8));
        writer.Write(bytes);
    }

    /// <summary>Encodes Java modified UTF-8 (U+0000 as C0 80; supplementary chars as surrogate pairs).</summary>
    /// <para>编码 Java 修改版 UTF-8（U+0000 写作 C0 80；增补字符按代理对处理）。</para>
    private static byte[] EncodeModifiedUtf(string value)
    {
        var length = 0;
        foreach (var ch in value) length += ch switch { '\0' => 2, <= '\u007F' => 1, <= '\u07FF' => 2, _ => 3 };
        if (length > 0xFFFF) throw new ArgumentException("别名过长（修改版 UTF-8 超过 65535 字节）。");
        var bytes = new byte[length];
        var offset = 0;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\0':
                    bytes[offset++] = 0xC0;
                    bytes[offset++] = 0x80;
                    break;
                case <= '\u007F':
                    bytes[offset++] = (byte)ch;
                    break;
                case <= '\u07FF':
                    bytes[offset++] = (byte)(0xC0 | (ch >> 6));
                    bytes[offset++] = (byte)(0x80 | (ch & 0x3F));
                    break;
                default:
                    bytes[offset++] = (byte)(0xE0 | (ch >> 12));
                    bytes[offset++] = (byte)(0x80 | ((ch >> 6) & 0x3F));
                    bytes[offset++] = (byte)(0x80 | (ch & 0x3F));
                    break;
            }
        }
        return bytes;
    }
}
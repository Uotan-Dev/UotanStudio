using System.Security.Cryptography;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>One decoded JKS entry: a private-key entry (tag 1) or a trusted-certificate entry (tag 2).</summary>
/// <para>单个解析出的 JKS 条目：私钥条目（tag 1）或受信证书条目（tag 2）。</para>
/// <param name="Tag">Raw entry type: 1 for a protected private-key entry, 2 for a trusted-certificate entry; <see cref="JksKeyStoreReader.Read"/> rejects any other value. <para>原始条目类型：1 为受保护私钥条目，2 为受信证书条目；<see cref="JksKeyStoreReader.Read"/> 拒绝其他值。</para></param>
/// <param name="Alias">Entry alias decoded from Java modified UTF-8. <para>由 Java 修改版 UTF-8 解码出的条目别名。</para></param>
/// <param name="Creation">Entry timestamp stored as big-endian milliseconds since the Unix epoch. <para>以大端 Unix 毫秒时间戳存储的条目时间。</para></param>
/// <param name="ProtectedPrivateKey">KeyProtector blob (salt, XOR keystream ciphertext, trailing SHA-1 check) for tag-1 entries; <see langword="null"/> for tag-2 entries. <para>tag-1 条目的 KeyProtector 数据块（盐、XOR 密钥流密文、尾部 SHA-1 校验）；tag-2 条目为 null。</para></param>
/// <param name="Certificates">DER-encoded certificates, leaf first; a single-element list for tag-2 entries. <para>DER 编码证书，叶子在前；tag-2 条目为单元素列表。</para></param>
public sealed record JksEntry(int Tag, string Alias, DateTimeOffset Creation, byte[]? ProtectedPrivateKey, IReadOnlyList<byte[]> Certificates);

/// <summary>
/// Reads JKS v1 keystores (magic 0xFEEDFEED), verifying the trailing integrity digest.
/// <para>读取 JKS v1 密钥库（magic 0xFEEDFEED）并校验尾部完整性摘要。</para>
/// </summary>
internal static class JksKeyStoreReader
{
    /// <summary>
    /// Parses all entries and verifies the trailing integrity digest.
    /// <para>解析全部条目并校验尾部完整性摘要。</para>
    /// </summary>
    /// <param name="data">Complete keystore bytes (for example the output of <see cref="JksKeyStoreWriter.Build"/>); the caller keeps ownership. <para>完整密钥库字节（例如 <see cref="JksKeyStoreWriter.Build"/> 的输出）；所有权归调用方。</para></param>
    /// <param name="password">Keystore password, encoded as big-endian UTF-16 pairs and keyed into the trailing "Mighty Aphrodite" SHA-1 digest. <para>密钥库密码，按大端 UTF-16 双字节编码并参与尾部 "Mighty Aphrodite" SHA-1 keyed 摘要。</para></param>
    /// <returns>All entries in file order; private-key entries keep the encrypted blob and are not decrypted here. <para>按文件顺序的全部条目；私钥条目保留加密数据块，此处不执行解密。</para></returns>
    /// <exception cref="InvalidDataException">The magic, version, entry count, a block length or the trailing digest is invalid; a digest mismatch reports a wrong password or a tampered keystore. <para>魔数、版本、条目数、某块长度或尾部摘要非法；摘要不匹配表示密码错误或密钥库被篡改。</para></exception>
    /// <exception cref="EndOfStreamException">The buffer ends in the middle of a field. <para>缓冲区在字段中间结束。</para></exception>
    public static IReadOnlyList<JksEntry> Read(byte[] data, string password)
    {
        var offset = 0;
        if (ReadInt32(data, ref offset) != JksKeyStoreWriter.Magic) throw new InvalidDataException("Not a JKS keystore (magic mismatch).");
        var version = ReadInt32(data, ref offset);
        if (version is not (1 or 2)) throw new InvalidDataException($"Unsupported JKS version: {version}.");
        var count = ReadInt32(data, ref offset);
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("JKS entry count is out of range.");

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
                    if (certCount is < 0 or > 16) throw new InvalidDataException("JKS certificate chain length is out of range.");
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
                    throw new InvalidDataException($"Unrecognized JKS entry type: {tag}.");
            }
        }

        digest.Append(data.AsSpan(bodyStart, offset - bodyStart));
        var computed = digest.GetHashAndReset();
        if (offset + computed.Length != data.Length) throw new InvalidDataException("JKS trailer length mismatch; the keystore is corrupted.");
        for (var i = 0; i < computed.Length; i++)
        {
            if (computed[i] != data[offset + i]) throw new InvalidDataException("Wrong password or the keystore has been tampered with.");
        }
        return entries;
    }

    /// <summary>
    /// Recovers the plaintext PKCS#8 private key from the KeyProtector blob stored in a tag-1
    /// entry (salt || XOR keystream ciphertext || SHA-1 integrity digest).
    /// <para>从 tag-1 条目存储的 KeyProtector 数据块（盐 || XOR 密钥流密文 || SHA-1 完整性摘要）
    /// 恢复明文 PKCS#8 私钥。</para>
    /// </summary>
    /// <param name="protectedKey">The <see cref="JksEntry.ProtectedPrivateKey"/> blob. <para>JksEntry.ProtectedPrivateKey 数据块。</para></param>
    /// <param name="password">Keystore password used when the entry was written. <para>写入条目时使用的密钥库密码。</para></param>
    /// <returns>The plaintext PKCS#8 DER key. <para>明文 PKCS#8 DER 私钥。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="protectedKey"/> is <see langword="null"/>. <para><paramref name="protectedKey"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The blob is shorter than salt plus digest, or the trailing SHA-1 check fails (wrong password or tampered entry); the partially recovered key is zeroed before throwing. <para>数据块短于盐加摘要长度，或尾部 SHA-1 校验失败（密码错误或条目被篡改）；抛出前会把已恢复的密钥清零。</para></exception>
    public static byte[] Unprotect(byte[] protectedKey, string password)
    {
        const int saltLength = 20;
        const int digestLength = 20;
        ArgumentNullException.ThrowIfNull(protectedKey);
        if (protectedKey.Length < saltLength + digestLength + 1)
            throw new InvalidDataException("Invalid JKS protected private key length.");
        var keyLength = protectedKey.Length - saltLength - digestLength;
        var passwordBytes = EncodePassword(password);
        try
        {
            var numRounds = (keyLength + digestLength - 1) / digestLength;
            var xorKey = new byte[keyLength];
            using var sha = Sha1.CreateAccumulator();
            var digest = protectedKey.AsSpan(0, saltLength).ToArray();
            for (var round = 0; round < numRounds; round++)
            {
                sha.Append(passwordBytes);
                sha.Append(digest);
                digest = sha.GetHashAndReset();
                Array.Copy(digest, 0, xorKey, round * digestLength, Math.Min(digestLength, xorKey.Length - (round * digestLength)));
            }

            var plainKey = new byte[keyLength];
            for (var i = 0; i < keyLength; i++) plainKey[i] = (byte)(protectedKey[saltLength + i] ^ xorKey[i]);
            sha.Append(passwordBytes);
            sha.Append(plainKey);
            var computed = sha.GetHashAndReset();
            if (!computed.AsSpan().SequenceEqual(protectedKey.AsSpan(saltLength + keyLength, digestLength)))
            {
                CryptographicOperations.ZeroMemory(plainKey);
                throw new InvalidDataException("Wrong password or the private key entry has been tampered with.");
            }
            return plainKey;
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    /// <summary>The keystore integrity digest: SHA1(passwordBytes || "Mighty Aphrodite").</summary>
    /// <para>密钥库完整性摘要：SHA1(passwordBytes || "Mighty Aphrodite")。</para>
    internal static DigestAccumulator IntegrityDigest(string password)
    {
        var hash = Sha1.CreateAccumulator();
        var passwordBytes = EncodePassword(password);
        hash.Append(passwordBytes);
        CryptographicOperations.ZeroMemory(passwordBytes);
        hash.Append("Mighty Aphrodite"u8);
        return hash;
    }

    /// <summary>Encodes every password char as a big-endian 16-bit pair (high byte first).</summary>
    /// <para>将密码的每个字符按大端 16 位写出（高字节在前）。</para>
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
        if (offset > data.Length - size) throw new EndOfStreamException("JKS data ended prematurely.");
        var value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        offset += size;
        return value;
    }

    private static long ReadInt64(byte[] data, ref int offset)
    {
        const int size = 8;
        if (offset > data.Length - size) throw new EndOfStreamException("JKS data ended prematurely.");
        long value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | data[offset + i];
        offset += size;
        return value;
    }

    private static int ReadInt16(byte[] data, ref int offset)
    {
        const int size = 2;
        if (offset > data.Length - size) throw new EndOfStreamException("JKS data ended prematurely.");
        var value = (data[offset] << 8) | data[offset + 1];
        offset += size;
        return value;
    }

    private static byte[] ReadBounded(byte[] data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);
        if (length < 0 || length > data.Length - offset) throw new InvalidDataException("JKS block length is out of bounds.");
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
        if (length > data.Length - offset) throw new InvalidDataException("JKS string length is out of bounds.");
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
                if (i + 1 >= length) throw new InvalidDataException("JKS string is truncated.");
                chars.Add((char)(((a & 0x1F) << 6) | (data[offset + i + 1] & 0x3F)));
                i += 2;
            }
            else if ((a & 0xF0) == 0xE0)
            {
                if (i + 2 >= length) throw new InvalidDataException("JKS string is truncated.");
                chars.Add((char)(((a & 0x0F) << 12) | ((data[offset + i + 1] & 0x3F) << 6) | (data[offset + i + 2] & 0x3F)));
                i += 3;
            }
            else throw new InvalidDataException("Invalid JKS string encoding.");
        }
        offset += length;
        return new string([.. chars]);
    }
}

/// <summary>
/// Writes JKS v1 keystores: big-endian framing, modified UTF-8 aliases, KeyProtector-protected
/// PKCS#8 private keys and the trailing "Mighty Aphrodite" SHA-1 keyed digest. AOT-safe and
/// reflection-free.
/// <para>写出 JKS v1 密钥库：大端框架、修改版 UTF-8 别名、KeyProtector 保护的 PKCS#8 私钥
/// 与尾部 "Mighty Aphrodite" SHA-1 keyed 摘要。AOT 安全、零反射。</para>
/// </summary>
public static class JksKeyStoreWriter
{
    /// <summary>JKS v1 magic shared by the writer and the reader's magic check.</summary>
    /// <para>JKS v1 魔数，供写出方与读取方的魔数校验共用。</para>
    internal const int Magic = unchecked((int)0xFEEDFEED);
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
    /// <exception cref="ArgumentException">The alias is empty or longer than 255 characters (or exceeds 65535 modified UTF-8 bytes), the password is empty, the private key is empty, or the chain is empty or holds a certificate of zero bytes or more than 4 MiB. <para>别名为空或超过 255 个字符（或修改版 UTF-8 超过 65535 字节）、密码为空、私钥为空，或证书链为空、包含 0 字节或超过 4 MiB 的证书。</para></exception>
    public static byte[] Build(string alias, byte[] pkcs8PrivateKey, IReadOnlyList<byte[]> certificateChain, string password, DateTimeOffset? creation = null)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 255) throw new ArgumentException("Alias must be 1–255 characters.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A keystore password must be provided.");
        if (pkcs8PrivateKey.Length == 0) throw new ArgumentException("The private key is empty.");
        if (certificateChain.Count == 0) throw new ArgumentException("The certificate chain cannot be empty.");
        foreach (var certificate in certificateChain)
        {
            if (certificate.Length == 0 || certificate.Length > 1 << 22) throw new ArgumentException("Certificate size is out of range.");
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
                    digest.Append(WriteBEBytes(new byte[4], value));
                }
                void WriteHashedInt16(int value)
                {
                    writer.WriteBEInt16(value);
                    Span<byte> bytes = [(byte)(value >> 8), (byte)value];
                    digest.Append(bytes);
                }
                void WriteHashedInt64(long value)
                {
                    writer.WriteBEInt64(value);
                    Span<byte> bytes = stackalloc byte[8];
                    for (var i = 0; i < 8; i++) bytes[i] = (byte)(value >> ((7 - i) * 8));
                    digest.Append(bytes);
                }
                void WriteHashed(ReadOnlySpan<byte> bytes)
                {
                    writer.Write(bytes);
                    digest.Append(bytes);
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
            using var sha = Sha1.CreateAccumulator();
            var digest = salt;
            for (var round = 0; round < numRounds; round++)
            {
                sha.Append(passwordBytes);
                sha.Append(digest);
                digest = sha.GetHashAndReset();
                var copyLength = Math.Min(DigestLength, xorKey.Length - (round * DigestLength));
                Array.Copy(digest, 0, xorKey, round * DigestLength, copyLength);
            }

            var protectedKey = new byte[SaltLength + plainKey.Length + DigestLength];
            Array.Copy(salt, protectedKey, SaltLength);
            for (var i = 0; i < plainKey.Length; i++) protectedKey[SaltLength + i] = (byte)(plainKey[i] ^ xorKey[i]);
            sha.Append(passwordBytes);
            sha.Append(plainKey);
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

    /// <summary>Encodes Java modified UTF-8 (U+0000 as C0 80; supplementary chars as surrogate pairs).</summary>
    /// <para>编码 Java 修改版 UTF-8（U+0000 写作 C0 80；增补字符按代理对处理）。</para>
    private static byte[] EncodeModifiedUtf(string value)
    {
        var length = 0;
        foreach (var ch in value) length += ch switch { '\0' => 2, <= '\u007F' => 1, <= '\u07FF' => 2, _ => 3 };
        if (length > 0xFFFF) throw new ArgumentException("Alias is too long (modified UTF-8 exceeds 65535 bytes).");
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

/// <summary>
/// Big-endian fixed-width writers used by <see cref="JksKeyStoreWriter"/> when framing JKS
/// fields; kept file-local because no other type writes big-endian JKS fields.
/// <para>供 <see cref="JksKeyStoreWriter"/> 写出 JKS 定宽大端字段；因没有其他类型写出大端 JKS 字段而保持文件局部。</para>
/// </summary>
file static class JksBigEndianWriterExtensions
{
    extension(BinaryWriter writer)
    {
        internal void WriteBEInt32(int value)
        {
            Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
            writer.Write(bytes);
        }

        internal void WriteBEInt16(int value)
        {
            Span<byte> bytes = [(byte)(value >> 8), (byte)value];
            writer.Write(bytes);
        }

        internal void WriteBEInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            for (var i = 0; i < 8; i++) bytes[i] = (byte)(value >> ((7 - i) * 8));
            writer.Write(bytes);
        }
    }
}
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

/// <summary>
/// Pins the ADB host identity to the shapes upstream <c>platform/packages/modules/adb</c> writes and reads:
/// PKCS#8 <c>adbkey</c>, a <c>adbkey.pub</c> sidecar with no trailing newline, the base64
/// <c>RSAPublicKey</c> wire payload terminated by a NUL, the 20-byte AUTH token, and the AUTH chain order
/// that ends with the user identity's public key.
/// <para>将 ADB 主机身份锁定在上游 <c>platform/packages/modules/adb</c> 读写这些形状：PKCS#8 的
/// <c>adbkey</c>、结尾无换行的 <c>adbkey.pub</c> 同名文件、以 NUL 结束的 base64 <c>RSAPublicKey</c>
/// 线上负载、20 字节的 AUTH 令牌，以及以用户身份公钥收尾的 AUTH 身份链顺序。</para>
/// </summary>
public sealed class AdbHostIdentityTests
{
    private const int EncodedPublicKeyBytes = 524;
    private const int Base64PublicKeyChars = 700;

    [Fact]
    public void LoadOrCreate_NewIdentity_WritesTheUpstreamPkcs8PrivateKeyFile()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "adbkey");

        using var identity = AdbAuthentication.LoadOrCreate(path);
        var text = File.ReadAllText(path);

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.EndsWith("-----END PRIVATE KEY-----\n", text, StringComparison.Ordinal);
        Assert.Equal(path, identity.KeyFilePath);
        using var reloaded = RSA.Create();
        reloaded.ImportFromPem(text);
        Assert.Equal(2048, reloaded.KeySize);
    }

    [Fact]
    public void LoadOrCreate_NewIdentity_PublicSidecarIsWirePayloadWithoutNewlineOrNul()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "adbkey");

        using var identity = AdbAuthentication.LoadOrCreate(path);
        var payload = identity.BuildPublicKeyPayload();
        var sidecar = File.ReadAllBytes(path + ".pub");

        Assert.Equal(payload[..^1], sidecar);
        Assert.NotEqual((byte)'\n', sidecar[^1]);
        Assert.DoesNotContain((byte)0, sidecar);
    }

    [Fact]
    public void BuildPublicKeyPayload_Default_IsBase64RsaStructureSpaceLabelThenNul()
    {
        using var identity = AdbAuthentication.CreateNew();

        var payload = Encoding.UTF8.GetString(identity.BuildPublicKeyPayload());
        var space = payload.IndexOf(' ');
        var encoded = payload[..space];
        var label = payload[(space + 1)..^1];

        Assert.Equal(Base64PublicKeyChars, encoded.Length);
        var decoded = Convert.FromBase64String(encoded);
        Assert.Equal(EncodedPublicKeyBytes, decoded.Length);
        Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(decoded[..4]));
        Assert.Equal(65537, BinaryPrimitives.ReadInt32LittleEndian(decoded[^4..]));
        Assert.Contains("@", label);
        Assert.Equal('\0', payload[^1]);
    }

    [Fact]
    public void SignToken_TokenIsTwentyBytesAndSignatureVerifiesAsRsaSha1Pkcs1()
    {
        using var identity = AdbAuthentication.CreateNew();
        var token = new byte[AdbAuthentication.TokenSize];
        RandomNumberGenerator.Fill(token);

        var signature = identity.SignToken(token);

        Assert.Equal(256, signature.Length);
        Assert.True(identity.RsaKey.VerifyData(token, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        Assert.False(identity.RsaKey.VerifyData(token, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(identity.RsaKey.VerifyData(token, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pss));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(32)]
    public void SignToken_TokenWidthOtherThanTwenty_RejectsAsProtocolError(int length)
    {
        using var identity = AdbAuthentication.CreateNew();

        Assert.Throws<ArgumentException>(() => identity.SignToken(new byte[length]));
    }

    [Fact]
    public void PublicKeyFingerprint_IsSha256OfDerSubjectPublicKeyInfo()
    {
        using var identity = AdbAuthentication.CreateNew();

        Assert.Equal(SHA256.HashData(identity.RsaKey.ExportSubjectPublicKeyInfo()), identity.PublicKeyFingerprint);
        Assert.Equal(32, identity.PublicKeyFingerprint.Length);
    }

    [Fact]
    public void LoadIdentityChain_TwoIdentities_AnnouncesTheUserKeyAfterSigningWithBoth()
    {
        using var transport = new FakeAdbDeviceTransport { AuthTokenChallenges = 3 };
        var userIdentity = AdbAuthentication.CreateNew();
        var vendorIdentity = AdbAuthentication.CreateNew();
        using var adb = new AdbSession(transport, [userIdentity, vendorIdentity]);

        adb.Connect();

        Assert.Equal(3, transport.AuthTokens.Count);
        Assert.Equal(2, transport.AuthSignatures.Count);
        Assert.True(userIdentity.RsaKey.VerifyData(
            transport.AuthTokens[0], transport.AuthSignatures[0], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        Assert.False(userIdentity.RsaKey.VerifyData(
            transport.AuthTokens[1], transport.AuthSignatures[1], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        Assert.True(vendorIdentity.RsaKey.VerifyData(
            transport.AuthTokens[1], transport.AuthSignatures[1], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        Assert.Equal(userIdentity.BuildPublicKeyPayload(), transport.AuthPublicKeyPayload);
    }

    [Fact]
    public void Load_MissingKeyFile_ThrowsAndWritesNothing()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "nested", "adbkey");

        Assert.Throws<FileNotFoundException>(() => AdbAuthentication.Load(path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".pub"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Load_CorruptKeyFile_ThrowsAndLeavesTheFileUntouched()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "adbkey");
        File.WriteAllText(path, "-----BEGIN PRIVATE KEY-----\nAAAAAAAA\n-----END PRIVATE KEY-----\n");
        var before = File.ReadAllBytes(path);

        Assert.ThrowsAny<CryptographicException>(() => AdbAuthentication.Load(path));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".pub"));
    }

    [Fact]
    public void LoadOrCreate_MissingIdentity_GeneratesAtTheUserPathLikeUpstreamLoadUserKey()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "adbkey");

        using var identity = AdbAuthentication.LoadOrCreate(path);

        Assert.Equal(path, identity.KeyFilePath);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".pub"));
    }

    [Fact]
    public void LoadIdentityChain_NoVendorKeys_ContainsOnlyTheUserKeyFile()
    {
        using var tempDir = new TempDirectory();
        var path = Path.Combine(tempDir.Path, "adbkey");

        var chain = AdbAuthentication.LoadIdentityChain(path);
        try
        {
            Assert.Single(chain);
            Assert.Equal(path, chain[0].KeyFilePath);
        }
        finally
        {
            foreach (var identity in chain)
            {
                identity.Dispose();
            }
        }
    }
}

/// <summary>
/// Covers <c>ADB_VENDOR_KEYS</c> enumeration, the mechanism upstream adb uses to widen the host identity
/// chain: entries are separated by <c>;</c> on Windows and <c>:</c> on the other platforms, a directory entry
/// contributes only files named <c>*.adb_key</c>, a direct file entry loads under any name, unreadable or
/// wrong-width keys are skipped, and duplicates collapse by public-key fingerprint. These tests mutate a
/// process environment variable, so they run in a collection that is never parallel with anything else.
/// <para>覆盖上游 adb 用于扩展主机身份链的 <c>ADB_VENDOR_KEYS</c> 枚举机制：条目在 Windows 上以
/// <c>;</c> 分隔、在其他平台上以 <c>:</c> 分隔；目录条目只取名为 <c>*.adb_key</c> 的文件，直接给出的文件
/// 不受名称限制，无法读取或位数不匹配的密钥会被跳过，重复密钥按公钥指纹合并。这些用例会修改进程环境变量，
/// 因此放在与其他测试永不并行的集合中。</para>
/// </summary>
[Collection("ADB vendor keys environment")]
public sealed class AdbVendorKeyChainTests
{
    [Fact]
    public void LoadIdentityChain_EnvironmentEntries_OrderAllVendorKeysByFingerprint()
    {
        using var tempDir = new TempDirectory();
        var userPath = Path.Combine(tempDir.Path, "adbkey");
        var vendorDirectory = Path.Combine(tempDir.Path, "vendor");
        Directory.CreateDirectory(vendorDirectory);
        var scanned = new List<byte[]>
        {
            WriteVendorKey(vendorDirectory, "alpha.adb_key", 2048),
            WriteVendorKey(vendorDirectory, "beta.adb_key", 2048),
        };
        WriteVendorKey(vendorDirectory, "ignored.pem", 2048);
        WriteVendorKey(vendorDirectory, "narrow.adb_key", 4096);
        var directKey = WriteVendorKey(tempDir.Path, "named-anything", 2048);

        var previous = Environment.GetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable);
        IReadOnlyList<AdbAuthentication> chain;
        try
        {
            var separator = OperatingSystem.IsWindows() ? ';' : ':';
            Environment.SetEnvironmentVariable(
                AdbAuthentication.VendorKeysEnvironmentVariable,
                string.Join(separator, [vendorDirectory, Path.Combine(tempDir.Path, "named-anything")]));
            chain = AdbAuthentication.LoadIdentityChain(userPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, previous);
        }

        try
        {
            Assert.Equal(4, chain.Count);
            Assert.Equal(userPath, chain[0].KeyFilePath);
            byte[][] expected = [.. scanned, directKey];
            Array.Sort(expected, static (left, right) => left.AsSpan().SequenceCompareTo(right));
            Assert.Equal(expected.Select(Convert.ToHexString),
                chain.Skip(1).Select(identity => Convert.ToHexString(identity.PublicKeyFingerprint)));
        }
        finally
        {
            foreach (var identity in chain)
            {
                identity.Dispose();
            }
        }
    }

    [Fact]
    public void LoadIdentityChain_VendorCopyOfUserKey_LoadsOneIdentityPerFingerprint()
    {
        using var tempDir = new TempDirectory();
        var userPath = Path.Combine(tempDir.Path, "adbkey");
        using var user = AdbAuthentication.LoadOrCreate(userPath);
        var vendorDirectory = Path.Combine(tempDir.Path, "vendor");
        Directory.CreateDirectory(vendorDirectory);
        File.Copy(userPath, Path.Combine(vendorDirectory, "same.adb_key"));

        var previous = Environment.GetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable);
        IReadOnlyList<AdbAuthentication> chain;
        try
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, vendorDirectory);
            chain = AdbAuthentication.LoadIdentityChain(userPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, previous);
        }

        try
        {
            Assert.Single(chain);
            Assert.Equal(user.PublicKeyFingerprint, chain[0].PublicKeyFingerprint);
        }
        finally
        {
            foreach (var identity in chain)
            {
                identity.Dispose();
            }
        }
    }

    [Fact]
    public void LoadIdentityChain_MissingVendorEntry_SkipsItWithoutGeneratingAKey()
    {
        using var tempDir = new TempDirectory();
        var userPath = Path.Combine(tempDir.Path, "adbkey");
        var absent = Path.Combine(tempDir.Path, "vendor", "issued.adb_key");

        var previous = Environment.GetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable);
        IReadOnlyList<AdbAuthentication> chain;
        try
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, absent);
            chain = AdbAuthentication.LoadIdentityChain(userPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, previous);
        }

        try
        {
            Assert.Single(chain);
            Assert.False(File.Exists(absent));
            Assert.False(File.Exists(absent + ".pub"));
            Assert.False(Directory.Exists(Path.GetDirectoryName(absent)!));
        }
        finally
        {
            foreach (var identity in chain)
            {
                identity.Dispose();
            }
        }
    }

    [Fact]
    public void LoadIdentityChain_CorruptVendorKeyInDirectory_SkippedWithoutSidecarRepair()
    {
        using var tempDir = new TempDirectory();
        var userPath = Path.Combine(tempDir.Path, "adbkey");
        var vendorDirectory = Path.Combine(tempDir.Path, "vendor");
        Directory.CreateDirectory(vendorDirectory);
        var corrupt = Path.Combine(vendorDirectory, "bad.adb_key");
        File.WriteAllText(corrupt, "-----BEGIN PRIVATE KEY-----\nAAAAAAAA\n-----END PRIVATE KEY-----\n");

        var previous = Environment.GetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable);
        IReadOnlyList<AdbAuthentication> chain;
        try
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, vendorDirectory);
            chain = AdbAuthentication.LoadIdentityChain(userPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdbAuthentication.VendorKeysEnvironmentVariable, previous);
        }

        try
        {
            Assert.Single(chain);
            Assert.False(File.Exists(corrupt + ".pub"));
        }
        finally
        {
            foreach (var identity in chain)
            {
                identity.Dispose();
            }
        }
    }

    private static byte[] WriteVendorKey(string directory, string fileName, int keyBits)
    {
        using var rsa = RSA.Create(keyBits);
        File.WriteAllText(Path.Combine(directory, fileName), rsa.ExportPkcs8PrivateKeyPem());
        return SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());
    }
}

[CollectionDefinition("ADB vendor keys environment", DisableParallelization = true)]
public sealed class AdbVendorKeyEnvironmentCollection;
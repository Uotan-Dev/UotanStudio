using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

public sealed class MtkSecCfgVersion3RewriteTests
{
    private const int BodyLength = 0x1860;
    private const byte WorkingMask = 0x5A;
    private const byte DecoyMask = 0xA5;
    private const uint AttributeLock = 0x6000;
    private const uint AttributeMpDefault = 0x6003;
    private const uint AttributeDefault = 0x33333333;
    private const uint AttributeUnlock = 0x44444444;
    private const uint StatusComplete = 0x43434343;

    [Fact]
    public async Task RewriteSecCfgVersion3Async_HardwareV3Body_PatchesAttributeThroughTargetEngine()
    {
        var plain = CreatePlainBody(AttributeUnlock);
        var image = BuildImage(Xor(plain, WorkingMask), MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);
        var engine = new MaskingHashEngine { WorkingKind = MtkSecCfgHashKind.HardwareV3 };

        var replacement = await MtkSession.RewriteSecCfgVersion3Async(report, image,
            MtkSecCfgLockIntent.Lock, engine, TestContext.Current.CancellationToken);

        (MtkSecCfgHashKind Kind, bool Encrypt)[] expectedCalls =
            [(MtkSecCfgHashKind.HardwareV3, false), (MtkSecCfgHashKind.HardwareV3, true)];
        Assert.Equal(expectedCalls, engine.Calls);
        Assert.Equal(0, replacement.Length % MtkSecCfg.ImageAlignment);
        var rewritten = ReadVersion3(replacement);
        Assert.Equal(MtkSecCfg.Version3ClosedEncryptionLength, rewritten.EncryptionLength);
        var rewrittenPlain = Xor(SliceBody(replacement, rewritten), WorkingMask);
        Assert.Equal(AttributeDefault, BinaryPrimitives.ReadUInt32LittleEndian(rewrittenPlain.AsSpan(0x858)));
        Assert.Equal(StatusComplete, BinaryPrimitives.ReadUInt32LittleEndian(rewrittenPlain.AsSpan(0x854)));
        Assert.Equal(plain[..0x850], rewrittenPlain[..0x850]);
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_LegacyVariantBody_ProbesDocumentedVariantOrder()
    {
        var plain = CreatePlainBody(AttributeLock);
        var image = BuildImage(Xor(plain, WorkingMask), MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);
        var engine = new MaskingHashEngine { WorkingKind = MtkSecCfgHashKind.HardwareV3Legacy };

        var replacement = await MtkSession.RewriteSecCfgVersion3Async(report, image,
            MtkSecCfgLockIntent.Unlock, engine, TestContext.Current.CancellationToken);

        (MtkSecCfgHashKind Kind, bool Encrypt)[] expectedCalls =
            [
                (MtkSecCfgHashKind.HardwareV3, false),
                (MtkSecCfgHashKind.Hardware, false),
                (MtkSecCfgHashKind.HardwareV3Legacy, false),
                (MtkSecCfgHashKind.HardwareV3Legacy, true)
            ];
        Assert.Equal(expectedCalls, engine.Calls);
        var rewrittenPlain = Xor(SliceBody(replacement, report), WorkingMask);
        Assert.Equal(AttributeUnlock, BinaryPrimitives.ReadUInt32LittleEndian(rewrittenPlain.AsSpan(0x858)));
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_SoftwareBody_RewritesOffDeviceWithoutAnyEngine()
    {
        var plain = CreatePlainBody(AttributeDefault);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3ClosedEncryptionLength);
        var report = ReadVersion3(image);

        var replacement = await MtkSession.RewriteSecCfgVersion3Async(report, image,
            MtkSecCfgLockIntent.Unlock, engine: null, TestContext.Current.CancellationToken);

        var rewritten = ReadVersion3(replacement);
        Assert.Equal(MtkSecCfg.Version3OpenEncryptionLength, rewritten.EncryptionLength);
        var rewrittenPlain = MtkSecCfg.SoftwareTransform(SliceBody(replacement, rewritten), encrypt: false);
        Assert.Equal(AttributeUnlock, BinaryPrimitives.ReadUInt32LittleEndian(rewrittenPlain.AsSpan(0x858)));
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_HardwareBodyWithoutEngine_RefusesSejRequirement()
    {
        var plain = CreatePlainBody(AttributeUnlock);
        var image = BuildImage(Xor(plain, WorkingMask), MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Lock, engine: null, TestContext.Current.CancellationToken));

        Assert.Contains("SEJ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_UnexplainedBody_RefusesWithoutFabricatingHash()
    {
        var plain = CreatePlainBody(AttributeUnlock);
        var image = BuildImage(Xor(plain, WorkingMask), MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);
        var engine = new MaskingHashEngine { WorkingKind = MtkSecCfgHashKind.Unresolved };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Lock, engine, TestContext.Current.CancellationToken));

        Assert.Contains("did not reproduce", error.Message, StringComparison.Ordinal);
        (MtkSecCfgHashKind Kind, bool Encrypt)[] expectedCalls =
            [
                (MtkSecCfgHashKind.HardwareV3, false),
                (MtkSecCfgHashKind.Hardware, false),
                (MtkSecCfgHashKind.HardwareV3Legacy, false)
            ];
        Assert.Equal(expectedCalls, engine.Calls);
    }

    [Theory]
    [InlineData(AttributeUnlock, MtkSecCfgLockIntent.Unlock)]
    [InlineData(AttributeDefault, MtkSecCfgLockIntent.Lock)]
    public async Task RewriteSecCfgVersion3Async_AlreadyInRequestedState_RefusesLikeDocumentedWriter(
        uint attribute, MtkSecCfgLockIntent intent)
    {
        var plain = CreatePlainBody(attribute);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3ClosedEncryptionLength);
        var report = ReadVersion3(image);

        await Assert.ThrowsAsync<InvalidOperationException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, intent, engine: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_LockFromNonUnlockAttribute_RefusesWithoutIo()
    {
        var plain = CreatePlainBody(AttributeMpDefault);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3ClosedEncryptionLength);
        var report = ReadVersion3(image);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Lock, engine: null, TestContext.Current.CancellationToken));

        Assert.Contains("no documented lock state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_SecondaryMarkerOffset_KeepsRefusalPendingReport()
    {
        var plain = CreatePlainBody(AttributeUnlock, primaryMarker: false);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Lock, engine: null, TestContext.Current.CancellationToken));

        Assert.Contains("0x850", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_UndocumentedStatusWord_RefusesLayout()
    {
        var plain = CreatePlainBody(AttributeUnlock);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(0x854), 0x11223344);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Lock, engine: null, TestContext.Current.CancellationToken));

        Assert.Contains("configuration status", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteSecCfgVersion3Async_BodyShorterThanFieldWindow_RefusesBeforeProbing()
    {
        var image = new byte[0x200];
        "AND_SECCFG_v\0\0\0\0"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10), MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), 0x130);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), MtkSecCfg.Version3ClosedEncryptionLength);
        for (var index = 0; index < 0x100; index++)
        {
            image[0x2C + index] = (byte)index;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x12C), MtkSecCfg.EndFlag);
        var report = ReadVersion3(image);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSession.RewriteSecCfgVersion3Async(
            report, image, MtkSecCfgLockIntent.Unlock, engine: null, TestContext.Current.CancellationToken));

        Assert.Contains("version 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSecCfgReplacementAsync_ViewWithoutEitherLayout_RefusesBeforeComputing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        var view = new MtkSecCfgView(MtkSecCfgFormat.Version4, 8, "seccfg", 3, 0, 0x200, "AB", null, null);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.CreateSecCfgReplacementAsync(
            view, new byte[0x200], MtkSecCfgLockIntent.Unlock, null, TestContext.Current.CancellationToken));

        Assert.Contains("neither a version 4 nor a version 3", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task CreateSecCfgReplacementAsync_SoftwareVersion3View_RewritesWithoutRegisterIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        var plain = CreatePlainBody(AttributeUnlock);
        var image = BuildImage(MtkSecCfg.SoftwareTransform(plain, encrypt: true),
            MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);
        var view = new MtkSecCfgView(MtkSecCfgFormat.Version3, 8, "seccfg", 3, 0, image.Length, "AB",
            null, report);

        var replacement = await session.CreateSecCfgReplacementAsync(view, image,
            MtkSecCfgLockIntent.Lock, null, TestContext.Current.CancellationToken);

        var rewritten = ReadVersion3(replacement);
        Assert.Equal(MtkSecCfg.Version3ClosedEncryptionLength, rewritten.EncryptionLength);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task CreateSecCfgReplacementAsync_HardwareVersion3ViewWithoutStageSeam_RefusesSejRequirement()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        var plain = CreatePlainBody(AttributeUnlock);
        var image = BuildImage(Xor(plain, WorkingMask), MtkSecCfg.Version3OpenEncryptionLength);
        var report = ReadVersion3(image);
        var view = new MtkSecCfgView(MtkSecCfgFormat.Version3, 8, "seccfg", 3, 0, image.Length, "AB",
            null, report);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.CreateSecCfgReplacementAsync(
            view, image, MtkSecCfgLockIntent.Lock, null, TestContext.Current.CancellationToken));

        Assert.Contains("SEJ", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    private static MtkSecCfgV3Report ReadVersion3(byte[] image)
    {
        Assert.True(MtkSecCfg.TryReadVersion3(image, out var report));
        Assert.NotNull(report);
        return report;
    }

    private static byte[] CreatePlainBody(uint attribute, bool primaryMarker = true)
    {
        var body = new byte[BodyLength];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = (byte)((index % 0x100) + 1);
        }

        if (primaryMarker)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 0x00000000u);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 0x11223344u);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x850),
            primaryMarker ? 0x00000007u : 0x49494949u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x854), StatusComplete);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x858), attribute);
        return body;
    }

    private static byte[] BuildImage(byte[] protectedBody, uint encryptionLength)
    {
        var declared = MtkSecCfg.Version3HeaderLength + protectedBody.Length + 4;
        var padded = (declared + MtkSecCfg.ImageAlignment - 1) / MtkSecCfg.ImageAlignment
            * MtkSecCfg.ImageAlignment;
        var image = new byte[padded];
        "AND_SECCFG_v\0\0\0\0"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10), MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), (uint)declared);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), encryptionLength);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x26), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x28), 7);
        protectedBody.CopyTo(image, MtkSecCfg.Version3HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(MtkSecCfg.Version3HeaderLength + protectedBody.Length), MtkSecCfg.EndFlag);
        return image;
    }

    private static byte[] SliceBody(byte[] image, MtkSecCfgV3Report report) =>
        image[MtkSecCfg.Version3HeaderLength..(MtkSecCfg.Version3HeaderLength + report.BodyLength)];

    private static byte[] Xor(byte[] data, byte mask)
    {
        var output = new byte[data.Length];
        for (var index = 0; index < data.Length; index++)
        {
            output[index] = (byte)(data[index] ^ mask);
        }

        return output;
    }

    private sealed class MaskingHashEngine : IMtkSecCfgHashEngine
    {
        public List<(MtkSecCfgHashKind Kind, bool Encrypt)> Calls { get; } = [];

        public MtkSecCfgHashKind WorkingKind { get; set; }

        public Task<byte[]> TransformAsync(MtkSecCfgHashKind kind, ReadOnlyMemory<byte> hash, bool encrypt,
            CancellationToken cancellationToken)
        {
            Calls.Add((kind, encrypt));
            return Task.FromResult(Xor(hash.ToArray(), kind == WorkingKind ? WorkingMask : DecoyMask));
        }
    }
}
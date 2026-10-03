using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

public sealed class MtkOemKeyRequestTests
{
    private const string Key32Hex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Fact]
    public void DecodeKey_SoftwareOneTimeProgramClass_ReturnsExactlyThirtyTwoBytes()
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, Key32Hex, 0x10001000);

        var key = request.DecodeKey();

        Assert.Equal(32, key.Length);
        Assert.Equal(MtkOemKeyRequest.KeyLength, key.Length);
        Assert.Equal(Key32Hex, Convert.ToHexString(key));
    }

    [Fact]
    public void DecodeKey_ReplayProtectedClass_NeedsNoHostSideBaseAddress()
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, Key32Hex, null);

        Assert.Equal(32, request.DecodeKey().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20")]
    [InlineData("ZZ0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E")]
    public void DecodeKey_PayloadOutsideTheDocumentedClass_RejectsInsteadOfPaddingOrTruncating(string? keyHex)
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, keyHex!, 0x10001000);

        var exception = Assert.Throws<ArgumentException>(() => request.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.KeyHex), exception.ParamName);
    }

    [Fact]
    public void DecodeKey_SoftwareClassWithoutBaseAddress_RejectsWithoutDefaultingIt()
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, Key32Hex, null);

        var exception = Assert.Throws<ArgumentException>(() => request.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.SejBaseAddress), exception.ParamName);
    }

    [Fact]
    public void DecodeKey_ReplayProtectedClassWithBaseAddress_RejectsIncompatibleFraming()
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, Key32Hex, 0x10001000);

        var exception = Assert.Throws<ArgumentException>(() => request.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.SejBaseAddress), exception.ParamName);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(uint.MaxValue - 0x60)]
    [InlineData(0xFFFF_FFE0)]
    public void DecodeKey_RegisterWindowWouldLeaveAddressSpace_RejectsInsteadOfWrapping(uint baseAddress)
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, Key32Hex, baseAddress);

        var exception = Assert.Throws<ArgumentException>(() => request.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.SejBaseAddress), exception.ParamName);
    }

    [Fact]
    public void DecodeKey_ZeroSejBaseAddress_IsAcceptedWithoutAdjustment()
    {
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, Key32Hex, 0x0000_0000);

        Assert.Equal(32, request.DecodeKey().Length);
    }

    [Fact]
    public void DecodeKey_UndefinedKeyClass_RejectsAsUndocumented()
    {
        var request = new MtkOemKeyRequest((MtkOemKeyKind)99, Key32Hex, 0x10001000);

        var exception = Assert.Throws<ArgumentException>(() => request.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.Kind), exception.ParamName);
        Assert.DoesNotContain("00010203", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterWindow_ListsTheEightDocumentedSoftwareOtpOffsets()
    {
        Assert.Equal(0x60u, MtkOemKeyRegisterWindow.Offset);
        Assert.Equal(8, MtkOemKeyRegisterWindow.SlotCount);
        uint[] expected = [0x60u, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C];
        Assert.Equal(string.Join(',', expected), string.Join(',', MtkOemKeyRegisterWindow.Offsets));
        Assert.Equal(MtkOemKeyRegisterWindow.SlotCount, MtkOemKeyRegisterWindow.Offsets.Count);
    }

    [Fact]
    public void RegisterWindow_ExplainReservedRegisters_NamesTheCommentedOutCallSite()
    {
        var reason = MtkOemKeyRegisterWindow.ExplainReservedRegisters();

        Assert.Contains("hwcrypto_sej.py:74-77", reason, StringComparison.Ordinal);
        Assert.Contains("hwcrypto_sej.py:623-626", reason, StringComparison.Ordinal);
        Assert.Contains("SECINIT", reason, StringComparison.Ordinal);
        Assert.Contains("MKJ", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_PreservesDigestAndUnitsWithoutRepeatingKeyMaterial()
    {
        var receipt = new MtkOemKeyReceipt(MtkOemKeyKind.ReplayProtectedAreaKey,
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9C41077E5F127609B7", 1, Key32Hex);

        Assert.Equal(MtkOemKeyKind.ReplayProtectedAreaKey, receipt.Kind);
        Assert.Equal(1, receipt.SubmittedUnits);
        Assert.Equal(Key32Hex, receipt.EchoHex);
        Assert.DoesNotContain(Key32Hex, receipt.KeySha256Hex, StringComparison.Ordinal);
    }
}

public sealed class MtkOemKeyPreloaderCarrierTests
{
    private const uint MarkerOffset = 0x40;

    [Fact]
    public void TryExtract_MarkerPresent_ReadsTheWindowBehindTheDocumentedMarker()
    {
        var preloader = BuildPreloaderWithKey(0x4D, MarkerOffset);

        Assert.True(MtkOemKeyPreloaderCarrier.TryExtract(preloader, out var key, out var markerOffset));
        Assert.Equal((int)MarkerOffset, markerOffset);
        Assert.NotNull(key);
        Assert.Equal(32, key!.Length);
        Assert.Equal(Convert.ToHexString(BuildKeyWindow()), Convert.ToHexString(key));
    }

    [Fact]
    public void TryExtract_MarkerAtStart_ReadsWindowAtFixedDistanceBehindMarker()
    {
        var preloader = BuildPreloaderWithKey(0x11, 0);

        Assert.True(MtkOemKeyPreloaderCarrier.TryExtract(preloader, out var key, out var markerOffset));
        Assert.Equal(0, markerOffset);
        Assert.NotNull(key);
        Assert.Equal(Convert.ToHexString(BuildKeyWindow()), Convert.ToHexString(key!));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("window-truncated")]
    [InlineData("empty")]
    public void TryExtract_MissingOrTruncatedWindow_ReportsNotFoundWithoutSubstitutingZeros(string shape)
    {
        var preloader = shape switch
        {
            "absent" => new byte[512],
            "window-truncated" => BuildMarkerNearEnd(),
            _ => []
        };
        var expectedOffset = shape == "window-truncated" ? 512 - 8 : -1;

        Assert.False(MtkOemKeyPreloaderCarrier.TryExtract(preloader, out var key, out var markerOffset));
        Assert.Null(key);
        Assert.Equal(expectedOffset, markerOffset);
    }

    private static byte[] BuildKeyWindow() => Enumerable.Range(0, 32).Select(index => (byte)(index * 3)).ToArray();

    private static byte[] BuildMarkerNearEnd()
    {
        var preloader = new byte[512];
        Array.Fill(preloader, (byte)0x4D);
        Convert.FromHexString("4D4D4D0130").CopyTo(preloader, 512 - 8);
        return preloader;
    }

    private static byte[] BuildPreloaderWithKey(byte fill, uint markerOffset)
    {
        var preloader = new byte[512];
        Array.Fill(preloader, fill);
        var offset = (int)markerOffset;
        Convert.FromHexString("4D4D4D0130").CopyTo(preloader, offset);
        BuildKeyWindow().CopyTo(preloader, offset + 0xC);
        return preloader;
    }
}
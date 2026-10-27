using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbDescriptorTableTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string _root => _temp.Path;

    [Fact]
    public async Task FromSignedImageAsync_HashFooterImage_ReportsSingleHashDescriptor()
    {
        var token = TestContext.Current.CancellationToken;
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4097, 31));
        var signedPath = Path.Combine(_root, "boot.signed.img");
        using var key = SigningTestKeys.Create();

        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);

        Assert.True(signResult.Success, signResult.ErrorMessage);
        var table = await AvbDescriptorTable.FromSignedImageAsync(signedPath, token);
        var entry = Assert.Single(table.Entries);
        Assert.Equal(2UL, entry.Tag);
        Assert.Equal("boot", entry.PartitionName);
        Assert.Equal(0, entry.Size % 8);
        Assert.True(entry.Size > (16 + 116));
    }

    [Fact]
    public async Task ReplaceContentDescriptors_SignedDescriptorTable_ReplacesEntryAndRetainsPropertyDescriptor()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var bootSource = _temp.WriteBytes("boot.img", BuildContent(4097, 17));
        var bootReplacement = _temp.WriteBytes("boot-replacement.img", BuildContent(5000, 23));
        var vendorSource = _temp.WriteBytes("vendor.img", BuildContent(5000, 29));
        var bootSigned = Path.Combine(_root, "boot.signed.img");
        var bootReplacementSigned = Path.Combine(_root, "boot-replacement.signed.img");
        var vendorSigned = Path.Combine(_root, "vendor.signed.img");
        var first = await AvbSigner.AddHashFooterAsync(bootSource, "boot", key, bootSigned, token);
        Assert.True(first.Success, first.ErrorMessage);
        var second = await AvbSigner.AddHashFooterAsync(
            bootReplacement, "boot", key, bootReplacementSigned, token);
        Assert.True(second.Success, second.ErrorMessage);
        var third = await AvbSigner.AddHashFooterAsync(vendorSource, "vendor", key, vendorSigned, token);
        Assert.True(third.Success, third.ErrorMessage);

        var templatePath = Path.Combine(_root, "template.bin");
        var firstVbmeta = ExtractVbmeta(await File.ReadAllBytesAsync(bootSigned, token));
        await File.WriteAllBytesAsync(templatePath, firstVbmeta, token);

        var propertyPath = Path.Combine(_root, "property.vbmeta");
        var propertyDescriptor = new AvbPropertyDescriptor
        {
            Key = "com.android.build.type",
            Value = "userdebug"u8.ToArray()
        };
        var propertySign = await AvbSigner.SignVbmetaAsync<AvbPropertyDescriptor>(
            templatePath, key, propertyPath, [propertyDescriptor], token);
        Assert.True(propertySign.Success, propertySign.ErrorMessage);
        var propertyTable = AvbDescriptorTable.Parse(await File.ReadAllBytesAsync(propertyPath, token));
        Assert.Equal(0UL, Assert.Single(propertyTable.Entries).Tag);

        var bootReplacementTable = AvbDescriptorTable.Parse(
            ExtractVbmeta(await File.ReadAllBytesAsync(bootReplacementSigned, token)));
        var vendorTable = await AvbDescriptorTable.FromSignedImageAsync(vendorSigned, token);
        var merged = propertyTable
            .ReplaceContentDescriptors("boot", bootReplacementTable)
            .ReplaceContentDescriptors("vendor", vendorTable);

        var finalPath = Path.Combine(_root, "vbmeta.final.img");
        var finalSign = await AvbSigner.SignVbmetaWithDescriptorTableAsync(
            templatePath, key, finalPath, merged, token);

        Assert.True(finalSign.Success, finalSign.ErrorMessage);
        Assert.Equal(3, finalSign.DescriptorCount);
        Assert.Equal("SHA256_RSA2048", finalSign.AlgorithmName);
        var finalBytes = await File.ReadAllBytesAsync(finalPath, token);
        Assert.Equal(finalBytes.Length, finalSign.OutputSize);
        var finalTable = AvbDescriptorTable.Parse(finalBytes);
        Assert.Equal(new ulong[] { 0, 2, 2 }, finalTable.Entries.Select(entry => entry.Tag).ToArray());
        Assert.Equal(new string?[] { null, "boot", "vendor" },
            finalTable.Entries.Select(entry => entry.PartitionName).ToArray());
        var finalDescriptors = AvbSignatureVerifier.ReadDescriptorsStrict(finalBytes);
        Assert.Equal(3, finalDescriptors.Count);
        var expectedBootDigest = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(
            ExtractVbmeta(await File.ReadAllBytesAsync(bootReplacementSigned, token)))).Digest;
        var bootDescriptor = Assert.Single(finalDescriptors, descriptor => descriptor.PartitionName == "boot");
        Assert.Equal(expectedBootDigest, bootDescriptor.Digest);
        var property = Assert.Single(finalDescriptors, descriptor => descriptor.Tag == 0);
        Assert.Equal("prop", property.TypeName);
        Assert.Equal("com.android.build.type", property.Key);
        Assert.Equal("userdebug"u8.ToArray(), property.Value);
        Assert.True(AvbSignatureVerifier.Verify(finalBytes).SignatureIsValid);
    }

    [Theory]
    [InlineData(4UL)]
    [InlineData(4096UL)]
    public void Parse_DescriptorLengthUnalignedOrOutOfRange_ThrowsInvalidDataException(ulong declaredLength)
    {
        var vbmeta = BuildVbmetaWithDescriptorEntry(declaredLength);

        Assert.Throws<InvalidDataException>(() => AvbDescriptorTable.Parse(vbmeta));
    }

    [Fact]
    public async Task FromSignedImageAsync_CanceledBeforeStart_ThrowsOperationCanceledException()
    {
        var token = TestContext.Current.CancellationToken;
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4096, 5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AvbDescriptorTable.FromSignedImageAsync(imagePath, cancellation.Token));
    }

    [Fact]
    public async Task SignVbmetaWithDescriptorTableAsync_CanceledBeforeStart_PreservesExistingOutput()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var imagePath = _temp.WriteBytes("boot.img", BuildContent(4097, 7));
        var signedPath = Path.Combine(_root, "boot.signed.img");
        var signResult = await AvbSigner.AddHashFooterAsync(imagePath, "boot", key, signedPath, token);
        Assert.True(signResult.Success, signResult.ErrorMessage);
        var templatePath = _temp.WriteBytes("template.bin",
            ExtractVbmeta(await File.ReadAllBytesAsync(signedPath, token)));
        var table = await AvbDescriptorTable.FromSignedImageAsync(signedPath, token);
        var outputPath = Path.Combine(_root, "final.img");
        await File.WriteAllBytesAsync(outputPath, [9, 8, 7], token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AvbSigner.SignVbmetaWithDescriptorTableAsync(
                templatePath, key, outputPath, table, cancellation.Token));

        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outputPath, token));
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] ExtractVbmeta(byte[] signedImage)
    {
        var footer = AvbFooter.TryLoad(signedImage);
        Assert.True(footer.HasValue);
        var offset = checked((int)footer.Value.VbmetaOffset);
        var size = checked((int)footer.Value.VbmetaSize);
        return signedImage[offset..(offset + size)];
    }

    private static byte[] BuildVbmetaWithDescriptorEntry(ulong declaredLength)
    {
        const int auxiliarySize = 128;
        var vbmeta = new byte[AvbVBMetaImageHeader.Size + auxiliarySize];
        "AVB0"u8.CopyTo(vbmeta);
        BinaryPrimitives.WriteUInt64BigEndian(vbmeta.AsSpan(20, 8), auxiliarySize);
        BinaryPrimitives.WriteUInt64BigEndian(vbmeta.AsSpan(104, 8), 16);
        var entry = vbmeta.AsSpan(AvbVBMetaImageHeader.Size, 16);
        BinaryPrimitives.WriteUInt64BigEndian(entry[..8], 2);
        BinaryPrimitives.WriteUInt64BigEndian(entry.Slice(8, 8), declaredLength);
        return vbmeta;
    }

    private static byte[] BuildContent(int length, int seed)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(seed + (index * 31));
        }

        return content;
    }
}
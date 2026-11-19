using UotanStudio.DD;
using UotanStudio.DD.Images;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class IsoImageValidatorTests
{
    [Fact]
    public void Validate_ValidIso_Accepts()
    {
        var path = CreateIso(17, validDescriptor: true, aligned: true);
        try
        {
            IsoImageValidator.Validate(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");

        Assert.Throws<FlashException>(() => IsoImageValidator.Validate(path));
    }

    [Fact]
    public void Validate_TooShort_Throws()
    {
        var path = CreateIso(16, validDescriptor: true, aligned: true);
        try
        {
            Assert.Throws<FlashException>(() => IsoImageValidator.Validate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_Unaligned_Throws()
    {
        var path = CreateIso(17, validDescriptor: true, aligned: false);
        try
        {
            Assert.Throws<FlashException>(() => IsoImageValidator.Validate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_NoPrimaryVolumeDescriptor_Throws()
    {
        var path = CreateIso(17, validDescriptor: false, aligned: true);
        try
        {
            Assert.Throws<FlashException>(() => IsoImageValidator.Validate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateIso(int sectors, bool validDescriptor, bool aligned)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
        var bytes = new byte[sectors * IsoImageValidator.SectorSize + (aligned ? 0 : 1)];
        if (validDescriptor && bytes.Length > IsoImageValidator.PrimaryVolumeDescriptorBlock * IsoImageValidator.SectorSize)
        {
            bytes[IsoImageValidator.PrimaryVolumeDescriptorBlock * IsoImageValidator.SectorSize] = 1;
            "CD001"u8.CopyTo(bytes.AsSpan(
                IsoImageValidator.PrimaryVolumeDescriptorBlock * IsoImageValidator.SectorSize + 1));
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }
}
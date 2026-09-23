using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashManifestReaderTests
{
    [Fact]
    public void Read_LegacyImageList_PreservesOrderAndCodeName()
    {
        using var fixture = new ManifestFixture();
        fixture.Image("boot.img");
        fixture.Image("vendor_boot.img");
        fixture.Write("codename: test_device\nvendor_boot\nboot images/boot.img");

        var result = FlashManifestReader.Read(fixture.Manifest);

        Assert.Equal("test_device", result.CodeName);
        Assert.Equal(["vendor_boot", "boot"], result.Images.Select(image => image.Partition));
        Assert.All(result.Images, image => Assert.Equal(1, image.Length));
    }

    [Theory]
    [InlineData("boot ../outside.img")]
    [InlineData("boot /tmp/image.img")]
    [InlineData("boot images/missing.img")]
    [InlineData("boot images/boot.img extra")]
    [InlineData("super_empty")]
    [InlineData("boot images/boot.img.zst")]
    [InlineData("codename: invalid-name\nboot")]
    [InlineData("boot images\\..\\outside.img")]
    [InlineData("super_empty delete")]
    [InlineData("super_empty create")]
    public void Read_InvalidOrUnsupportedEntry_RejectsEntireManifest(string contents)
    {
        using var fixture = new ManifestFixture();
        fixture.Image("boot.img");
        fixture.Write(contents);

        Assert.Throws<InvalidDataException>(() => FlashManifestReader.Read(fixture.Manifest));
    }

    [Fact]
    public void Read_LogicalOperations_PreservesManifestOrder()
    {
        using var fixture = new ManifestFixture();
        fixture.Image("boot.img");
        fixture.Write("system delete\nsystem create\nboot");

        var result = FlashManifestReader.Read(fixture.Manifest);

        Assert.Equal(["delete", "create", "flash"], result.Steps.Select(step => step.Command));
        Assert.Equal(["system", "system", "boot"], result.Steps.Select(step => step.Partition));
    }

    [Fact]
    public void Read_ValidSuperEmpty_PreservesWipeStepWithoutForcingFastbootd()
    {
        using var fixture = new ManifestFixture();
        fixture.Image("super_empty.img", FlashSuperEmptyServiceTests.EmptyMetadata());
        fixture.Write("super_empty");

        var plan = FlashManifestReader.Read(fixture.Manifest);

        Assert.False(plan.RequiresFastbootd);
        Assert.Equal("wipe_super", Assert.Single(plan.Steps).Command);
    }

    [Fact]
    public void Read_ExplicitFastbootdSuperEmpty_PreservesMode()
    {
        using var fixture = new ManifestFixture();
        fixture.Image("super_empty.img", FlashSuperEmptyServiceTests.EmptyMetadata());
        fixture.Write("mode:fastbootd\nsuper_empty");

        Assert.True(FlashManifestReader.Read(fixture.Manifest).RequiresFastbootd);
    }

    private sealed class ManifestFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flash-manifest-" + Guid.NewGuid().ToString("N"));
        public string Manifest => Path.Combine(_root, "fastboot.txt");

        public ManifestFixture() => Directory.CreateDirectory(Path.Combine(_root, "images"));

        public void Image(string name, byte[]? bytes = null) =>
            File.WriteAllBytes(Path.Combine(_root, "images", name), bytes ?? [1]);

        public void Write(string contents) => File.WriteAllText(Manifest, contents);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

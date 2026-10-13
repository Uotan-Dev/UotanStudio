using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsXattrIndexTests
{
    [Theory]
    [InlineData(1, "user.note")]
    [InlineData(2, "system.posix_acl_access")]
    [InlineData(3, "system.posix_acl_default")]
    [InlineData(4, "trusted.note")]
    [InlineData(6, "security.note")]
    public void Encode_DefinedShortPrefixIndex_RoundTripsThroughParseInline(int expectedIndex, string name)
    {
        var entry = new ErofsXattrEntry { Name = name, Value = [1, 2, 3] };

        var encoding = ErofsXattr.Encode([entry], null);
        var icount = ErofsXattr.GetICountForIbodyLength(encoding.Ibody.Length);
        var parsed = ErofsXattr.ParseInline(encoding.Ibody, icount);

        var actual = Assert.Single(parsed);
        Assert.Equal(name, actual.Name);
        Assert.Equal((byte)expectedIndex, actual.NameIndex);
        Assert.Equal(new byte[] { 1, 2, 3 }, actual.Value);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void Encode_DeclaredNameIndexWithoutNamespace_ThrowsFileSystemUnsupportedFeatureException(int index)
    {
        var entries = new List<ErofsXattrEntry> { new() { Name = "user.note", NameIndex = (byte)index } };

        Assert.Throws<FileSystemUnsupportedFeatureException>(() => ErofsXattr.Encode(entries, null));
    }

    [Fact]
    public void EncodeInline_DeclaredReservedNameIndex_ThrowsFileSystemUnsupportedFeatureException()
    {
        var entries = new List<ErofsXattrEntry> { new() { Name = "user.note", NameIndex = ErofsXattr.ReservedNameIndex } };

        Assert.Throws<FileSystemUnsupportedFeatureException>(() => ErofsXattr.EncodeInline(entries));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void ParseInline_HandBuiltIbodyWithUndefinedNameIndex_ThrowsFileSystemCorruptedException(int index)
    {
        var ibody = new byte[20];
        ibody[12] = 1;
        ibody[13] = (byte)index;
        ibody[16] = (byte)'x';

        Assert.Throws<FileSystemCorruptedException>(() => ErofsXattr.ParseInline(ibody, 3));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void ResolveName_UndefinedNameIndex_ThrowsFileSystemCorruptedException(int index)
    {
        Assert.Throws<FileSystemCorruptedException>(() => ErofsXattr.ResolveName((byte)index, "x"u8, null));
    }

    [Fact]
    public void Encode_SystemPrefixedName_RegistersLongPrefixInsteadOfUndefinedIndex()
    {
        var entry = new ErofsXattrEntry { Name = "system.foo", Value = [] };

        var encoding = ErofsXattr.Encode([entry], null);

        Assert.NotEmpty(encoding.PrefixTable);
        var icount = ErofsXattr.GetICountForIbodyLength(encoding.Ibody.Length);
        var parsed = Assert.Single(ErofsXattr.ParseInline(encoding.Ibody, icount, encoding.PrefixTable));
        Assert.Equal("system.foo", parsed.Name);
    }
}
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Apple;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Round-trip and bound checks for the Apple plist codec; no transport or device is involved.
/// <para>Apple 属性列表编解码的往返与上限校验；不涉及传输与设备。</para>
/// </summary>
public sealed class ApplePlistTests
{
    [Fact]
    public void RoundTrip_XmlDictionary_PreservesValues()
    {
        var root = new Dictionary<string, object>
        {
            ["string"] = "value",
            ["int"] = 1234567L,
            ["real"] = 1.5d,
            ["flag"] = true,
            ["data"] = new byte[] { 1, 2, 3, 0xFF },
            ["list"] = new List<object> { 1L, "two", false },
            ["nested"] = new Dictionary<string, object> { ["deep"] = "中文值" },
        };

        var parsed = Assert.IsType<Dictionary<string, object>>(ApplePlist.Parse(ApplePlist.ToXml(root)));

        Assert.Equal("value", parsed["string"]);
        Assert.Equal(1234567L, parsed["int"]);
        Assert.Equal(1.5d, parsed["real"]);
        Assert.Equal(true, parsed["flag"]);
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF }, (byte[])parsed["data"]);
        Assert.Equal(1234567L, parsed["int"]);
        var list = Assert.IsType<List<object>>(parsed["list"]);
        Assert.Equal(3, list.Count);
        var nested = Assert.IsType<Dictionary<string, object>>(parsed["nested"]);
        Assert.Equal("中文值", nested["deep"]);
    }

    [Fact]
    public void RoundTrip_BinaryDictionary_PreservesValuesAndNonAscii()
    {
        var root = new Dictionary<string, object>
        {
            ["key"] = "值-Value",
            ["negative"] = -42L,
            ["big"] = long.MaxValue,
            ["blob"] = new byte[] { 0xDE, 0xAD },
        };

        var parsed = Assert.IsType<Dictionary<string, object>>(ApplePlist.Parse(ApplePlist.ToBinary(root)));

        Assert.Equal("值-Value", parsed["key"]);
        Assert.Equal(-42L, parsed["negative"]);
        Assert.Equal(long.MaxValue, parsed["big"]);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, (byte[])parsed["blob"]);
    }

    [Fact]
    public void CrossFormat_BinaryWritten_ParsesAsXmlReadablePayload()
    {
        var root = new Dictionary<string, object> { ["k"] = 1L };
        var binary = ApplePlist.ToBinary(root);

        Assert.True(ApplePlist.LooksBinary(binary));
        Assert.False(ApplePlist.LooksBinary(ApplePlist.ToXml(root)));
    }

    [Fact]
    public void Parse_TruncatedBinaryHeader_Throws()
    {
        var payload = new byte[] { (byte)'b' };
        Assert.Throws<InvalidDataException>(() => ApplePlist.Parse(payload));
    }

    [Fact]
    public void Parse_BinaryWithAbsurdLength_ThrowsInsteadOfAllocating()
    {
        var payload = new List<byte>();
        payload.AddRange("bplist00"u8);
        payload.Add(0xD0);
        payload.Add(0x4F);
        payload.AddRange(new byte[] { 0x11, 0xFF, 0xFF });
        payload.AddRange(new byte[32]);
        Assert.Throws<InvalidDataException>(() => ApplePlist.Parse([.. payload]));
    }

    [Fact]
    public void Parse_XmlWithDtd_Throws()
    {
        const string payload = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd" [
              <!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">
            ]>
            <plist version="1.0"><dict><key>k</key><string>&xxe;</string></dict></plist>
            """;

        Assert.ThrowsAny<Exception>(() => ApplePlist.Parse(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void BinaryRoundTrip_DateAndUid_Preserved()
    {
        var date = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var root = new Dictionary<string, object>
        {
            ["when"] = date,
            ["uid"] = new ApplePlistUid(42),
        };

        var parsed = Assert.IsType<Dictionary<string, object>>(ApplePlist.Parse(ApplePlist.ToBinary(root)));

        var parsedDate = Assert.IsType<DateTime>(parsed["when"]);
        Assert.Equal(date, parsedDate, TimeSpan.FromSeconds(1));
        Assert.Equal(42ul, Assert.IsType<ApplePlistUid>(parsed["uid"]).Value);
    }

    [Fact]
    public void BinaryHeader_TopObjectIndexReadFromTrailer()
    {
        var binary = ApplePlist.ToBinary(new Dictionary<string, object> { ["k"] = 1L });

        var trailer = binary[^32..];
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64BigEndian(trailer.AsSpan(16, 8)));
        Assert.True(BinaryPrimitives.ReadUInt64BigEndian(trailer.AsSpan(24, 8)) < (ulong)binary.Length);
    }
}
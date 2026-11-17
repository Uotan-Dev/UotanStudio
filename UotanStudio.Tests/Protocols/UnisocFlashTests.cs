using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Unisoc;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// PAC-driven plan tests: a synthetic package written to the host temporary directory exercises the public PAC
/// surface, the XML loader-scheme resolution and the plan's flashable-image list, including the rejection rules.
/// <para>PAC 计划测试：写到主机临时目录的合成包驱动公共 PAC 面、XML 加载器 scheme 解析与计划的可刷写镜像清单，
/// 含各项拒绝规则。</para>
/// </summary>
public class UnisocFlashTests : IDisposable
{
    private static readonly string TemporaryDirectory =
        Path.Combine(Path.GetTempPath(), "uotan-unisoc-flash-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _files = [];

    [Fact]
    public void Load_WellFormedPackage_ResolvesLoadersAndImages()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml",
                Encoding.ASCII.GetBytes("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <BMAConfig>
                      <SchemeList>
                        <Scheme>
                          <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block></File>
                          <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block></File>
                        </Scheme>
                      </SchemeList>
                    </BMAConfig>
                    """)),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
            new PackageEntry("system", "system.img", PackagePayload(24)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Equal("FDL", plan.Loaders.FirstStage.Id);
        Assert.Equal(0x5500u, plan.Loaders.FirstStageAddress);
        Assert.Equal("FDL2", plan.Loaders.SecondStage.Id);
        Assert.Equal(0x9EFFFE00u, plan.Loaders.SecondStageAddress);
        Assert.Equal(2, plan.Images.Count);
        Assert.Equal("boot", plan.Images[0].Name);
        Assert.Equal(16, plan.Images[0].Length);
        Assert.Equal("system", plan.Images[1].Name);
        Assert.Equal(24, plan.Images[1].Length);
        using var stream = plan.OpenImage(plan.Images[0]);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0xA5, reader.ReadByte());
    }

    [Fact]
    public void Load_DuplicateFirstStageIdentifier_IsRejected()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1a.bin", PackagePayload(4)),
            new PackageEntry("FDL1", "fdl1b.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Load_MissingLoaderScheme_IsRejected()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Load_AddressPlusLoaderBeyondRam_IsRejected()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0xFFFFFFFF", "0x9EFFFE00")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Load_DuplicateImageNames_AreRejected()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00")),
            new PackageEntry("boot", "boot-a.img", PackagePayload(16)),
            new PackageEntry("BOOT", "boot-b.img", PackagePayload(16)),
        ]);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Options_FullEraseWithoutWipePhrase_IsRefusedBeforeAnyIo()
    {
        var options = new UnisocFlashOptions
        {
            Mode = UnisocFlashMode.FullErase,
            FlashConfirmation = UnisocFlashOptions.ConfirmationPhrase,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);

        var confirmed = new UnisocFlashOptions
        {
            Mode = UnisocFlashMode.FullErase,
            FlashConfirmation = UnisocFlashOptions.ConfirmationPhrase,
            WipeConfirmation = UnisocFlashOptions.WipeConfirmationPhrase,
        };
        Assert.Equal(UnisocFlashOptions.ConfirmationPhrase, confirmed.Validate());
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(TemporaryDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] SchemeXml(string first, string second) => Encoding.ASCII.GetBytes($$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <BMAConfig>
          <SchemeList>
            <Scheme>
              <File><ID>FDL</ID><Block><Base>{{first}}</Base></Block></File>
              <File><ID>FDL2</ID><Block><Base>{{second}}</Base></Block></File>
            </Scheme>
          </SchemeList>
        </BMAConfig>
        """);

    private static byte[] PackagePayload(int length)
    {
        var payload = new byte[length];
        Array.Fill(payload, (byte)0xA5);
        return payload;
    }

    private string WritePackage(IReadOnlyList<PackageEntry> entries)
    {
        Directory.CreateDirectory(TemporaryDirectory);
        var path = Path.Combine(TemporaryDirectory, $"package-{Guid.NewGuid():N}.pac");
        var headerSize = 2124;
        var entrySize = 2580;
        var payloadOffset = headerSize + entries.Count * entrySize;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), (uint)(payloadOffset + entries.Sum(entry => entry.Payload.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1076), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1080), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2116), 0xFFFAFFFAu);
        stream.Write(header);
        var offset = (uint)payloadOffset;
        foreach (var entry in entries)
        {
            var record = new byte[entrySize];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)entrySize);
            WriteUtf16(record.AsSpan(4, 512), entry.Id);
            WriteUtf16(record.AsSpan(516, 512), entry.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1540), (uint)entry.Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1544), (uint)entry.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1552), offset);
            stream.Write(record);
            offset += (uint)entry.Payload.Length;
        }

        foreach (var entry in entries)
        {
            stream.Write(entry.Payload);
        }

        _files.Add(path);
        return path;
    }

    private static void WriteUtf16(Span<byte> destination, string value)
    {
        for (var i = 0; i < value.Length && i * 2 + 1 < destination.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * 2, 2), value[i]);
        }
    }

    private sealed record PackageEntry(string Id, string Name, byte[] Payload, int Type = 1);
}
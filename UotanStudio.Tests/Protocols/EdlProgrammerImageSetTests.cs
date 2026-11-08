using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Cases for the multi-image Sahara programmer: the <c>qsahara_device_programmer.xml</c> manifest that names one
/// image per Sahara image identifier, and the handshake that uploads several of them in one exchange.
/// <para>多镜像 Sahara programmer 的用例：为每个 Sahara 镜像标识命名一个镜像的
/// <c>qsahara_device_programmer.xml</c> 清单，以及在一次交换中上传其中多个镜像的握手。</para>
/// </summary>
/// <remarks>
/// The manifest is untrusted input, so the cases cover the bounds as well as the happy path: duplicate ids,
/// missing references, an unusable root and an over-large declaration all have to be refused before any image is
/// opened for upload.
/// <para>清单是不可信输入，因此用例既覆盖正常路径也覆盖边界：重复标识、缺失引用、不可用的根元素与超量声明都必须在
/// 任何镜像被打开上传之前拒绝。</para>
/// </remarks>
public sealed class EdlProgrammerImageSetTests : IDisposable
{
    private const int ElfHeaderSize = 52;
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint ReadDataCommand = EdlConstants.SaharaCmdReadData;
    private const uint EndOfImageCommand = EdlConstants.SaharaCmdEndOfImage;
    private const uint DoneCommand = EdlConstants.SaharaCmdDone;
    private const uint DoneResponseCommand = EdlConstants.SaharaCmdDoneResponse;
    private const uint ImageTxCompleteStatus = (uint)SaharaMode.ImageTxComplete;
    private const uint DeviceHelloVersion = 2;
    private const uint DeviceHelloMinVersion = 1;
    private const uint DeviceHelloMaxCommandLength = 0x400;

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "uotan-edl-image-set-" + Guid.NewGuid().ToString("N"));

    public EdlProgrammerImageSetTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Read_RelativeImages_ResolvesThemAgainstTheManifestDirectoryInDocumentOrder()
    {
        var first = WriteFile("first.elf", BuildElf());
        var second = WriteFile("second.mbn", BuildElf());
        var manifest = WriteManifest("qsahara_device_programmer.xml",
            ("13", Path.GetFileName(first)), ("36", Path.GetFileName(second)));

        var images = SaharaProgrammerManifest.Read(manifest);

        Assert.Equal(2, images.Count);
        Assert.Equal(13u, images[0].ImageId);
        Assert.Equal(Path.GetFullPath(first), images[0].Path);
        Assert.Equal(36u, images[1].ImageId);
        Assert.Equal(new FileInfo(first).Length, images[0].LengthBytes);
    }

    [Fact]
    public void Read_AbsoluteImagePath_IsHonoured()
    {
        var image = WriteFile("xbl_sc.elf", BuildElf());
        var manifest = WriteManifest("absolute.xml", ("21", Path.GetFullPath(image)));

        var images = SaharaProgrammerManifest.Read(manifest);

        var single = Assert.Single(images);
        Assert.Equal(21u, single.ImageId);
        Assert.Equal(Path.GetFullPath(image), single.Path);
    }

    [Fact]
    public void Read_RelativePathThatOnlyExistsAsGiven_IsAccepted()
    {
        var nested = Path.Combine(_root, "sub");
        Directory.CreateDirectory(nested);
        var image = WriteFile("sequencer_ram.elf", BuildElf());
        var manifest = WriteManifest(Path.Combine(nested, "asgiven.xml"), ("59", image));

        var images = SaharaProgrammerManifest.Read(manifest);

        Assert.Equal(Path.GetFullPath(image), Assert.Single(images).Path);
    }

    [Fact]
    public void Read_DuplicateImageId_IsRefused()
    {
        var manifest = WriteManifest("duplicate.xml", ("13", "a.elf"), ("13", "b.elf"));
        WriteFile("a.elf", BuildElf());
        WriteFile("b.elf", BuildElf());

        Assert.Throws<InvalidDataException>(() => SaharaProgrammerManifest.Read(manifest));
    }

    [Theory]
    [InlineData("thirteen")]
    [InlineData("0xZZ")]
    [InlineData("")]
    public void Read_NonNumericImageId_IsRefused(string imageId)
    {
        var manifest = WriteManifest("badid.xml", (imageId, "p.elf"));
        WriteFile("p.elf", BuildElf());

        Assert.Throws<InvalidDataException>(() => SaharaProgrammerManifest.Read(manifest));
    }

    [Fact]
    public void Read_HexadecimalImageId_IsParsed()
    {
        var manifest = WriteManifest("hex.xml", ("0x24", "p.elf"));
        WriteFile("p.elf", BuildElf());

        Assert.Equal(0x24u, Assert.Single(SaharaProgrammerManifest.Read(manifest)).ImageId);
    }

    [Fact]
    public void Read_MissingReferencedImage_IsRefused()
    {
        var manifest = WriteManifest("missing.xml", ("13", "absent.elf"));

        var error = Assert.Throws<InvalidDataException>(() => SaharaProgrammerManifest.Read(manifest));

        Assert.Contains("absent.elf", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<not_sahara_config><images><image image_id=\"13\" image_path=\"p.elf\"/></images></not_sahara_config>")]
    [InlineData("<sahara_config><chipset>x</chipset></sahara_config>")]
    [InlineData("<sahara_config><images/></sahara_config>")]
    public void Read_UnusableDocumentStructure_IsRefused(string document)
    {
        WriteFile("p.elf", BuildElf());
        var manifest = Path.Combine(_root, "structure.xml");
        File.WriteAllText(manifest, "<?xml version=\"1.0\" ?>" + document);

        Assert.Throws<InvalidDataException>(() => SaharaProgrammerManifest.Read(manifest));
    }

    [Fact]
    public void Read_MoreImagesThanTheCeiling_IsRefused()
    {
        var images = Enumerable.Range(1, SaharaProgrammerManifest.MaximumImages + 1)
            .Select(index => (ImageId: index.ToString(System.Globalization.CultureInfo.InvariantCulture), ImagePath: "p.elf"))
            .ToArray();
        WriteFile("p.elf", BuildElf());
        var manifest = WriteManifest("too-many.xml", images);

        Assert.Throws<InvalidDataException>(() => SaharaProgrammerManifest.Read(manifest));
    }

    [Fact]
    public void Read_ManifestWithoutXmlExtension_IsNotRecognizedAsOne()
    {
        var manifest = WriteManifest("programmer.txt", ("13", "p.elf"));
        WriteFile("p.elf", BuildElf());

        Assert.False(SaharaProgrammerManifest.IsManifestPath(manifest));
        Assert.True(SaharaProgrammerManifest.IsManifestPath(Path.ChangeExtension(manifest, ".XML")));
    }

    [Fact]
    public void Open_SingleElfFile_ProducesOneFirehoseImage()
    {
        var image = WriteFile("prog_firehose_ddr.elf", BuildElf());

        var set = EdlProgrammerImageSet.Open(image);

        Assert.False(set.IsManifest);
        Assert.Equal(Path.GetFullPath(image), set.SourcePath);
        var single = Assert.Single(set.Images);
        Assert.Equal((uint)SaharaImageId.Firehose, single.ImageId);
    }

    [Fact]
    public void Open_Manifest_ProducesEveryDeclaredImage()
    {
        var first = WriteFile("prog_firehose_ddr.elf", BuildElf());
        var second = WriteFile("multi_image.mbn", BuildElf());
        var manifest = WriteManifest("qsahara.xml",
            ("13", Path.GetFileName(first)), ("37", Path.GetFileName(second)));

        var set = EdlProgrammerImageSet.Open(manifest);

        Assert.True(set.IsManifest);
        Assert.Equal([13u, 37u], set.Images.Select(image => image.ImageId));
    }

    [Fact]
    public void Open_MissingSingleImage_IsRefused()
    {
        var missing = Path.Combine(_root, "absent.elf");

        Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.Open(missing));
    }

    /// <remarks>
    /// The fixture is a real vendor manifest and stays outside the repository, so the case is environment-gated
    /// instead of being hard-coded to one machine's path.
    /// <para>该夹具是真实厂商清单且不在仓库内，因此用例以环境变量门控，而不是写死某台机器的路径。</para>
    /// </remarks>
    [Fact]
    public void Read_RealVendorManifestSample_ParsesEveryDeclaredImage()
    {
        const string sampleVariable = "UOTAN_SAHARA_MANIFEST_SAMPLE";
        var sample = Environment.GetEnvironmentVariable(sampleVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(sample) && File.Exists(sample),
            $"{sampleVariable} is not set to an existing qsahara_device_programmer.xml path; " +
            "set it to the vendor manifest to run this fixture case.");

        var images = SaharaProgrammerManifest.Read(sample!);

        Assert.True(images.Count >= 2, $"The sample declared only {images.Count} images.");
        Assert.Equal(images.Count, images.Select(image => image.ImageId).Distinct().Count());
        Assert.All(images, image => Assert.True(File.Exists(image.Path),
            $"Declared image {image.ImageId} is missing at {image.Path}."));
        Assert.All(images, image => Assert.True(image.LengthBytes > 0));
    }

    /// <remarks>
    /// Proves the protocol layer really keys READ_DATA by image id: the device asks for two different ids and
    /// each range has to be answered from the matching stream.
    /// <para>证实协议层确实按镜像标识索引 READ_DATA：设备请求两个不同标识，每个区段都必须由对应的流应答。</para>
    /// </remarks>
    [Fact]
    public async Task HandshakeAsync_TwoImageIds_UploadsEachRangeFromTheMatchingStream()
    {
        var transport = new ScriptedCommTransport();
        var firstId = (uint)SaharaImageId.Firehose;
        const uint secondId = 36u;
        transport.Enqueue(Hello(SaharaMode.ImageTxPending));
        transport.Enqueue(ReadData(firstId, 0, 8));
        transport.Enqueue(EndOfImage(firstId, 0));
        transport.Enqueue(DoneResponse((uint)SaharaMode.ImageTxPending));
        transport.Enqueue(Hello(SaharaMode.ImageTxPending));
        transport.Enqueue(ReadData(secondId, 0, 8));
        transport.Enqueue(EndOfImage(secondId, 0));
        transport.Enqueue(DoneResponse(ImageTxCompleteStatus));
        using var first = new MemoryStream(Enumerable.Repeat((byte)0xA5, 8).ToArray());
        using var second = new MemoryStream(Enumerable.Repeat((byte)0x5A, 8).ToArray());
        var images = new Dictionary<uint, Stream> { [firstId] = first, [secondId] = second };
        var sahara = new SaharaProtocol(transport);
        try
        {
            var mode = await sahara.HandshakeAsync(images, TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            var payload = transport.Writes.Where(IsTransferPayload).ToArray();
            Assert.Equal(2, payload.Length);
            Assert.Equal((byte)0xA5, payload[0][0]);
            Assert.Equal((byte)0x5A, payload[1][0]);
        }
        finally
        {
            sahara.Dispose();
        }
    }

    /// <remarks>
    /// A transferred range is not a Sahara packet: every packet begins with one of the protocol command words, so
    /// the payload is the write that begins with neither. Filtering by length alone would also catch DONE.
    /// <para>传输区段不是 Sahara 数据包：每个数据包都以协议命令字开头，因此负载就是开头两者都不是的那次写入。
    /// 只按长度过滤也会把 DONE 算进来。</para>
    /// </remarks>
    private static bool IsTransferPayload(byte[] write) => write.Length > 4
        && BinaryPrimitives.ReadUInt32LittleEndian(write) is not (
            (uint)SaharaCommand.HelloResponse or (uint)SaharaCommand.Done or (uint)SaharaCommand.ResetState);

    [Fact]
    public void FromImages_MultiImageMapping_BuildsManifestSetWithMeasuredLengths()
    {
        var first = WriteFile("multi_image.mbn", BuildElf());
        var second = WriteFile("prog_firehose_ddr.elf", BuildElf());

        var set = EdlProgrammerImageSet.FromImages(_root, [
            new SaharaProgrammerImage(37, first, 999),
            new SaharaProgrammerImage(13, second, 1)
        ]);

        Assert.True(set.IsManifest);
        Assert.Equal(Path.GetFullPath(_root), set.SourcePath);
        Assert.Equal(2, set.Images.Count);
        Assert.Equal(new FileInfo(first).Length, set.Images[0].LengthBytes);
        Assert.Equal(Path.GetFullPath(first), set.Images[0].Path);
    }

    [Fact]
    public void FromImages_SingleImage_BuildsPlainImageSet()
    {
        var image = WriteFile("prog_ufs_firehose.elf", BuildElf());

        var set = EdlProgrammerImageSet.FromImages(_root,
            [new SaharaProgrammerImage((uint)SaharaImageId.Firehose, image, 64)]);

        Assert.False(set.IsManifest);
        Assert.Equal(Path.GetFullPath(image), set.Images[0].Path);
    }

    [Fact]
    public void FromImages_RepeatedImageId_IsRejected()
    {
        var image = WriteFile("xbl.elf", BuildElf());

        Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.FromImages(_root, [
            new SaharaProgrammerImage(21, image, 16),
            new SaharaProgrammerImage(21, image, 16)
        ]));
    }

    [Fact]
    public void FromImages_MissingImage_IsRejected()
    {
        var image = WriteFile("xbl.elf", BuildElf());

        Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.FromImages(_root, [
            new SaharaProgrammerImage(21, image, 16),
            new SaharaProgrammerImage(13, Path.Combine(_root, "absent.elf"), 16)
        ]));
    }

    [Fact]
    public void FromImages_EmptyImage_IsRejected()
    {
        var image = WriteFile("empty.elf", []);

        Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.FromImages(_root,
            [new SaharaProgrammerImage(13, image, 0)]));
    }

    [Fact]
    public void FromImages_AboveImageCountCeiling_IsRejected()
    {
        var image = WriteFile("xbl.elf", BuildElf());
        var images = Enumerable.Range(0, SaharaProgrammerManifest.MaximumImages + 1)
            .Select(index => new SaharaProgrammerImage((uint)index, image, 16))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.FromImages(_root, images));
    }

    [Fact]
    public void FromImages_EmptyMapping_IsRejected()
        => Assert.Throws<InvalidDataException>(() => EdlProgrammerImageSet.FromImages(_root, []));

    [Fact]
    public void FromImages_BlankSourcePath_IsRejected()
    {
        var image = WriteFile("xbl.elf", BuildElf());

        Assert.Throws<ArgumentException>(() => EdlProgrammerImageSet.FromImages(" ",
            [new SaharaProgrammerImage(21, image, 16)]));
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string WriteManifest(string name, params (string ImageId, string ImagePath)[] images)
    {
        var path = Path.Combine(_root, name);
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" ?>",
            "<sahara_config>",
            "\t<chipset>test</chipset>",
            "\t<images>"
        };
        lines.AddRange(images.Select(image =>
            $"\t\t<image image_id=\"{image.ImageId}\" image_path=\"{image.ImagePath}\" />"));
        lines.Add("\t</images>");
        lines.Add("</sahara_config>");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static byte[] Hello(SaharaMode mode)
    {
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, HelloCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), DeviceHelloVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), DeviceHelloMinVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), DeviceHelloMaxCommandLength);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), (uint)mode);
        return packet;
    }

    private static byte[] ReadData(uint imageId, uint offset, uint length)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ReadDataCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), imageId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), length);
        return packet;
    }

    private static byte[] EndOfImage(uint imageId, uint status)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, EndOfImageCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), imageId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), status);
        return packet;
    }

    private static byte[] DoneResponse(uint status)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, DoneResponseCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), status);
        return packet;
    }

    private static byte[] BuildElf()
    {
        var header = new byte[ElfHeaderSize];
        "\u007fELF"u8.CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        header[6] = 1;
        header[16] = 2;
        header[18] = 40;
        header[20] = 1;
        header[40] = ElfHeaderSize;
        return header;
    }
}
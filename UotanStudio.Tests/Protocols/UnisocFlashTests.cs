using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Firmware.VendorFirmware.Pac;
using UotanStudio.ViewModels.Flash;
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
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "boot", "system")),
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
        Assert.Equal("boot", plan.Images[0].Partition);
        Assert.Equal(16, plan.Images[0].Length);
        Assert.Equal("system", plan.Images[1].Name);
        Assert.Equal("system", plan.Images[1].Partition);
        Assert.Equal(24, plan.Images[1].Length);
        using var stream = plan.OpenImage(plan.Images[0]);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0xA5, reader.ReadByte());
    }

    [Fact]
    public void Load_SchemeDeclaresEraseStep_PlanListsEraseEntryWithoutPayload()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", EraseSchemeXml("0x00005500", "0x9EFFFE00", "EraseMisc")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var erase = Assert.Single(plan.Images);
        Assert.Equal("EraseMisc", erase.Name);
        Assert.Equal("EraseMisc", erase.Partition);
        Assert.True(erase.EraseOnly);
        Assert.Equal(0, erase.Length);
        Assert.Throws<ArgumentException>(() => plan.OpenImage(erase));
    }

    [Fact]
    public void Load_SchemeDeclaresPayloadFreeInspectionStep_PlanOmitsIt()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", InspectionSchemeXml("0x00005500", "0x9EFFFE00", "PhaseCheck", "boot")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var image = Assert.Single(plan.Images);
        Assert.Equal("boot", image.Name);
        Assert.False(image.EraseOnly);
        Assert.Equal(16, image.Length);
    }

    [Fact]
    public void Load_ProductNameMatchesOneScheme_SelectsThatScheme()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml",
                TwoSchemeXml("ums512_1h10", "boot", "ums512_2h10", "system")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
            new PackageEntry("system", "system.img", PackagePayload(24)),
        ], productName: "ums512_2h10");

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var image = Assert.Single(plan.Images);
        Assert.Equal("system", image.Name);
    }

    [Fact]
    public void Load_ProductNameAbsent_FallsBackToFirstSchemeWithLoaders()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml",
                TwoSchemeXml("ums512_1h10", "boot", "ums512_2h10", "system")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
            new PackageEntry("system", "system.img", PackagePayload(24)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var image = Assert.Single(plan.Images);
        Assert.Equal("boot", image.Name);
    }

    [Fact]
    public void Load_ProductNameMatchesNoScheme_FallsBackToFirstSchemeWithLoaders()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml",
                TwoSchemeXml("ums512_1h10", "boot", "ums512_2h10", "system")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
            new PackageEntry("system", "system.img", PackagePayload(24)),
        ], productName: "absent_product");

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var image = Assert.Single(plan.Images);
        Assert.Equal("boot", image.Name);
    }

    [Fact]
    public void TakesSelectAll_EraseRow_IsExcludedSoBulkTickingCannotWipe()
    {
        var erase = new FlashPartitionEntry { Name = "EraseMisc", IsDestructiveErase = true };
        var script = new FlashPartitionEntry { Name = "reboot", IsScriptStep = true };
        var plain = new FlashPartitionEntry { Name = "boot" };

        Assert.False(FlashPartitionEntry.TakesSelectAll(erase));
        Assert.False(FlashPartitionEntry.TakesSelectAll(script));
        Assert.True(FlashPartitionEntry.TakesSelectAll(plain));
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
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "boot", "BOOT")),
            new PackageEntry("boot", "boot-a.img", PackagePayload(16)),
            new PackageEntry("BOOT", "boot-b.img", PackagePayload(16)),
        ]);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Load_HighOffsetEntry_ReadsPayloadFromPastFourGiB()
    {
        // A package above 4 GiB relocates selected payloads past the 32-bit wrap boundary and records that in
        // the entry's offset high word. The reader must follow each entry's own flag instead of assuming a
        // fixed relocation.
        var scheme = SchemeXml("0x00005500", "0x9EFFFE00", "boot", "system");
        var path = WritePackageWithHighOffset([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4), HighOffset: false),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4), HighOffset: false),
            new PackageEntry("XML", "scheme.xml", scheme, HighOffset: true),
            new PackageEntry("boot", "boot.img", PackagePayload(16), HighOffset: true),
            new PackageEntry("system", "system.img", PackagePayload(24), HighOffset: false),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Equal("FDL", plan.Loaders.FirstStage.Id);
        Assert.Equal(0x5500u, plan.Loaders.FirstStageAddress);
        Assert.Equal("FDL2", plan.Loaders.SecondStage.Id);
        Assert.Equal(0x9EFFFE00u, plan.Loaders.SecondStageAddress);
        Assert.Equal(2, plan.Images.Count);

        // boot declares the high word, system does not; both must read their real payload.
        Assert.Equal("boot", plan.Images[0].Name);
        using (var stream = plan.OpenImage(plan.Images[0]))
        {
            var payload = new byte[16];
            stream.ReadExactly(payload);
            Assert.All(payload, static value => Assert.Equal(0xA5, value));
        }

        Assert.Equal("system", plan.Images[1].Name);
        using (var stream = plan.OpenImage(plan.Images[1]))
        {
            var payload = new byte[24];
            stream.ReadExactly(payload);
            Assert.All(payload, static value => Assert.Equal(0xA5, value));
        }
    }

    [Fact]
    public void Load_RealPackage_FollowsSchemeOrderNotDirectoryOrder()
    {
        const string variable = "UOTAN_SPRD_PAC";
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Skip($"Set {variable} to a real SPRD/UNISOC PAC image to run this case.");
        }

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Equal("FDL", plan.Loaders.FirstStage.Id);
        Assert.Equal(0x5500u, plan.Loaders.FirstStageAddress);
        Assert.Equal("FDL2", plan.Loaders.SecondStage.Id);
        Assert.Equal(0x9EFFFE00u, plan.Loaders.SecondStageAddress);
        Assert.NotEmpty(plan.Images);

        using var pac = PacImage.Open(path);
        var directoryOrder = pac.Entries
            .Where(entry => entry.Type == PacFileType.Image && entry.Length > 0)
            .Select(entry => entry.Id)
            .ToArray();
        var planOrder = plan.Images.Where(image => !image.EraseOnly).Select(image => image.Name).ToArray();

        Assert.True(
            directoryOrder.ToHashSet(StringComparer.Ordinal).SetEquals(planOrder),
            "the scheme must select exactly the image payloads the package carries");
        Assert.NotEqual(directoryOrder, planOrder);
        Assert.Contains("NV_LTE", planOrder);
        Assert.Contains("ProdNV", planOrder);

        var steps = SprdPacLoaders.ResolveScheme(pac);
        var payloadSteps = steps.Where(step => step.CarriesPayload).ToArray();
        Assert.Equal(planOrder, payloadSteps.Select(step => step.Identifier).ToArray());
        Assert.Contains(steps, step => !step.CarriesPayload);

        var eraseEntries = plan.Images.Where(image => image.EraseOnly).ToArray();
        Assert.Equal(
            steps.Where(step => !step.CarriesPayload && step.IsErase && step.Partition.Length > 0)
                .Select(step => step.Identifier),
            eraseEntries.Select(image => image.Name));
        Assert.All(eraseEntries, image => Assert.Equal(0, image.Length));
    }

    [Fact]
    public void Load_PackageEntryOutsideScheme_IsNotListedAsImage()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "BOOT", "Super")),
            new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
            new PackageEntry("NV_LTE", "nvitem.bin", PackagePayload(8)),
            new PackageEntry("Super", "super.img", PackagePayload(32)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, plan.Images.Count);
        Assert.Equal("BOOT", plan.Images[0].Name);
        Assert.Equal("Super", plan.Images[1].Name);
        Assert.DoesNotContain(plan.Images, image => image.Name == "NV_LTE");
    }

    [Fact]
    public void Load_SchemeOrder_DecidesImageOrder()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "Super", "BOOT")),
            new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
            new PackageEntry("Super", "super.img", PackagePayload(32)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Equal(["Super", "BOOT"], plan.Images.Select(static image => image.Name).ToArray());
        Assert.Equal(["Super", "BOOT"], plan.Images.Select(static image => image.Partition).ToArray());
    }

    [Fact]
    public void Load_SchemeStepWithoutPayload_IsSkippedButKeepsPayloadStepsIntact()
    {
        var xml = Encoding.ASCII.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <BMAConfig>
              <SchemeList>
                <Scheme>
                  <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag></File>
                  <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag></File>
                  <File><ID>EraseMisc</ID><Block id="misc"><Base>0x0</Base></Block><Flag>0</Flag></File>
                  <File><ID>BOOT</ID><Block id="boot"><Base>0x0</Base></Block><Flag>1</Flag></File>
                </Scheme>
              </SchemeList>
            </BMAConfig>
            """);
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", xml),
            new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        Assert.Single(plan.Images);
        Assert.Equal("BOOT", plan.Images[0].Name);
        Assert.Equal("boot", plan.Images[0].Partition);

        var steps = SprdPacLoaders.ResolveScheme(PacImage.Open(path));
        Assert.Equal(2, steps.Count);
        Assert.Equal("EraseMisc", steps[0].Identifier);
        Assert.False(steps[0].CarriesPayload);
        Assert.True(steps[1].CarriesPayload);
    }

    [Fact]
    public void Load_SchemeStepWithoutMatchingPayload_IsRejected()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "BOOT", "Missing")),
            new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
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

    private static byte[] SchemeXml(string first, string second, params string[] imageIdentifiers) =>
        Encoding.ASCII.GetBytes(BuildSchemeXml(first, second, imageIdentifiers));

    private static byte[] EraseSchemeXml(string first, string second, params string[] eraseIdentifiers) =>
        Encoding.ASCII.GetBytes(BuildSchemeXml(first, second, [], eraseIdentifiers));

    private static byte[] InspectionSchemeXml(string first, string second, string inspection, string image) =>
        Encoding.ASCII.GetBytes(BuildSchemeXml(first, second, [image], inspectionIdentifiers: [inspection]));

    private static byte[] TwoSchemeXml(
        string firstName, string firstImage, string secondName, string secondImage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("<BMAConfig>");
        builder.AppendLine("  <ProductList>");
        foreach (var name in new[] { firstName, secondName })
        {
            builder.AppendLine($$"""
                    <Product name="{{name}}"><SchemeName>{{name}}</SchemeName></Product>
        """);
        }

        builder.AppendLine("  </ProductList>");
        builder.AppendLine("  <SchemeList>");
        foreach (var (name, image) in new[] { (firstName, firstImage), (secondName, secondImage) })
        {
            builder.AppendLine($$"""    <Scheme name="{{name}}">""");
            builder.AppendLine($$"""
                      <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
                """);
            builder.AppendLine($$"""
                      <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
                """);
            builder.AppendLine($$"""
                      <File><ID>{{image}}</ID><Block id="{{image}}"><Base>0x0</Base></Block><Type>CODE2</Type><Flag>1</Flag><CheckFlag>0</CheckFlag></File>
                """);
            builder.AppendLine("    </Scheme>");
        }

        builder.AppendLine("  </SchemeList>");
        builder.AppendLine("</BMAConfig>");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string BuildSchemeXml(
        string first,
        string second,
        IReadOnlyList<string> imageIdentifiers,
        IReadOnlyList<string>? eraseIdentifiers = null,
        string? productName = null,
        IReadOnlyList<string>? inspectionIdentifiers = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("<BMAConfig>");
        if (productName is not null)
        {
            builder.AppendLine("  <ProductList>");
            builder.AppendLine($$"""
                    <Product name="{{productName}}"><SchemeName>{{productName}}</SchemeName></Product>
        """);
            builder.AppendLine("  </ProductList>");
        }

        builder.AppendLine("  <SchemeList>");
        builder.AppendLine(productName is null ? "    <Scheme>" : $$"""    <Scheme name="{{productName}}">""");
        builder.AppendLine($$"""
                  <File><ID>FDL</ID><Block><Base>{{first}}</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
            """);
        builder.AppendLine($$"""
                  <File><ID>FDL2</ID><Block><Base>{{second}}</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
            """);
        foreach (var identifier in imageIdentifiers)
        {
            builder.AppendLine($$"""
                      <File><ID>{{identifier}}</ID><Block id="{{identifier}}"><Base>0x0</Base></Block><Type>CODE2</Type><Flag>1</Flag><CheckFlag>0</CheckFlag></File>
                """);
        }

        foreach (var identifier in inspectionIdentifiers ?? [])
        {
            builder.AppendLine($$"""
                      <File><ID>{{identifier}}</ID><Block id="{{identifier}}"><Base>0x0</Base></Block><Type>CODE2</Type><Flag>0</Flag><CheckFlag>2</CheckFlag></File>
                """);
        }

        foreach (var identifier in eraseIdentifiers ?? [])
        {
            builder.AppendLine($$"""
                      <File><ID>{{identifier}}</ID><Block id="{{identifier}}"><Base>0x0</Base></Block><Type>EraseFlash2</Type><Flag>0</Flag><CheckFlag>2</CheckFlag></File>
                """);
        }

        builder.AppendLine("    </Scheme>");
        builder.AppendLine("  </SchemeList>");
        builder.AppendLine("</BMAConfig>");
        return builder.ToString();
    }

    private static byte[] PackagePayload(int length)
    {
        var payload = new byte[length];
        Array.Fill(payload, (byte)0xA5);
        return payload;
    }

    private string WritePackage(IReadOnlyList<PackageEntry> entries, string? productName = null)
    {
        Directory.CreateDirectory(TemporaryDirectory);
        var path = Path.Combine(TemporaryDirectory, $"package-{Guid.NewGuid():N}.pac");
        var headerSize = 2124;
        var entrySize = 2580;
        var payloadOffset = headerSize + entries.Count * entrySize;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = new byte[headerSize];
        if (productName is not null)
        {
            WriteUtf16(header.AsSpan(52, 512), productName);
        }

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

    /// <summary>
    /// Writes a package that mirrors the above-4-GiB layout: entries flagged <see cref="PackageEntry.HighOffset"/>
    /// keep their real payload past the 32-bit wrap boundary and record that in the offset high word, while the
    /// low range holds a placeholder. Unflagged entries stay entirely below the boundary.
    /// <para>写出与「超过 4 GiB」布局一致的包：带 <see cref="PackageEntry.HighOffset"/> 标记的条目把真实负载放在
    /// 32 位回绕边界之外并在偏移高位字中记录，低位范围只保留占位；未标记的条目仍完全位于边界以下。</para>
    /// </summary>
    /// <param name="entries">Entries to write. <para>要写入的条目。</para></param>
    /// <returns>The created package path. <para>创建出的包路径。</para></returns>
    private string WritePackageWithHighOffset(IReadOnlyList<PackageEntry> entries)
    {
        const long relocatedBase = 1L << 32;
        Directory.CreateDirectory(TemporaryDirectory);
        var path = Path.Combine(TemporaryDirectory, $"high-offset-{Guid.NewGuid():N}.pac");
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
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1536), entry.HighOffset ? 1u : 0u);
            stream.Write(record);
            offset += (uint)entry.Payload.Length;
        }

        foreach (var entry in entries)
        {
            if (entry.HighOffset)
            {
                stream.Write(new byte[entry.Payload.Length]);
            }
            else
            {
                stream.Write(entry.Payload);
            }
        }

        stream.Position = relocatedBase + payloadOffset;
        foreach (var entry in entries)
        {
            stream.Write(entry.HighOffset ? entry.Payload : new byte[entry.Payload.Length]);
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

    private sealed record PackageEntry(
        string Id,
        string Name,
        byte[] Payload,
        int Type = 1,
        bool HighOffset = false);
}
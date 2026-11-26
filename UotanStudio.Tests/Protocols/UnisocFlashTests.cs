using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Firmware.VendorFirmware.Pac;
using UotanStudio.Hash.Crc16Arc;
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
    public void BuildRows_ListsBothLoadersAndMapsStepsToFlashOrErase()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml",
                Encoding.ASCII.GetBytes(BuildSchemeXml("0x00005500", "0x9EFFFE00", ["boot"], ["EraseMisc"]))),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);
        var rows = FlashViewModel.BuildUnisocPlanRows(plan, "remaining");

        var fdl1 = Assert.Single(rows, row => row.Command == "FDL1");
        Assert.True(fdl1.IsScriptStep);
        Assert.True(fdl1.IsSelected);
        Assert.Equal("0x00005500", fdl1.Offset);
        Assert.Equal("PAC:fdl1.bin", fdl1.FileName);

        var fdl2 = Assert.Single(rows, row => row.Command == "FDL2");
        Assert.True(fdl2.IsScriptStep);
        Assert.Equal("0x9EFFFE00", fdl2.Offset);

        var write = Assert.Single(rows, row => row.Name == "boot");
        Assert.Equal("flash", write.Command);
        Assert.False(write.IsDestructiveErase);

        var erase = Assert.Single(rows, row => row.IsDestructiveErase);
        Assert.Equal("erase", erase.Command);
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
    public void Load_CorruptDataCrc_IsRejectedBeforeAnyDeviceIo()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "BOOT")),
            new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
        ]);
        var bytes = File.ReadAllBytes(path);
        bytes[2124] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Load_MandatorySchemeStep_IsCarriedIntoPlan()
    {
        var xml = Encoding.ASCII.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <BMAConfig>
              <SchemeList>
                <Scheme>
                  <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
                  <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
                  <File><ID>BOOT</ID><Block id="boot"><Base>0x0</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
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

        Assert.True(plan.Images.Single(image => image.Name == "BOOT").IsMandatory);
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

    [Fact]
    public void Load_SchemeDeclaresBlockBaseSizeAndAlias_PlanCarriesThemForTheGrid()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", LayoutSchemeXml()),
            new PackageEntry("ProdNV", "prodnv.img", PackagePayload(16)),
            new PackageEntry("EraseMisc", "", []),
        ]);

        using var plan = UnisocFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var payload = plan.Images.Single(image => image.Name == "ProdNV");
        Assert.Equal("ProdNV", payload.Alias);
        Assert.Equal("CODE2", payload.Operation);
        Assert.Equal(0x2000L, payload.Offset);
        Assert.Equal(0xA00000L, payload.DeclaredSize);
        Assert.Equal(1, payload.FlashType);
        Assert.Equal("prodnv.img", payload.PackagedFileName);
        Assert.False(payload.EraseOnly);

        var erase = plan.Images.Single(image => image.Name == "EraseMisc");
        Assert.True(erase.EraseOnly);
        Assert.Equal("EraseFlash2", erase.Operation);
        Assert.Equal(0x4000L, erase.Offset);
        Assert.Equal(0L, erase.DeclaredSize);
        Assert.Equal(string.Empty, erase.PackagedFileName);
    }

    [Fact]
    public void ResolveLayout_ProductDeclaresFlashType1_ReportsNandFlash()
    {
        var path = WritePackage(
            [
                new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
                new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
                new PackageEntry("XML", "scheme.xml", LayoutSchemeXml()),
                new PackageEntry("ProdNV", "prodnv.img", PackagePayload(16)),
                new PackageEntry("EraseMisc", "", []),
            ],
            "ums512_1h10");

        using var package = PacImage.Open(path);
        var layout = SprdPacLoaders.ResolveLayout(package);

        Assert.Equal(1, layout.FlashType);
        Assert.Contains(layout.Steps, step => step.Identifier == "ProdNV" && step.Alias == "ProdNV");
    }

    private static byte[] LayoutSchemeXml() => Encoding.ASCII.GetBytes("""
        <?xml version="1.0" encoding="UTF-8"?>
        <BMAConfig>
          <ProductList>
            <Product name="ums512_1h10"><SchemeName>ums512_1h10</SchemeName><FlashTypeID>1</FlashTypeID></Product>
          </ProductList>
          <SchemeList>
            <Scheme name="ums512_1h10">
              <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>ProdNV</ID><IDAlias>ProdNV</IDAlias><Type>CODE2</Type><Block id="prodnv"><Base>0x2000</Base><Size>0xA00000</Size></Block><Flag>1</Flag><CheckFlag>2</CheckFlag></File>
              <File><ID>EraseMisc</ID><IDAlias>EraseMisc</IDAlias><Type>EraseFlash2</Type><Block id="misc"><Base>0x4000</Base><Size>0x0</Size></Block><Flag>0</Flag><CheckFlag>2</CheckFlag></File>
            </Scheme>
          </SchemeList>
        </BMAConfig>
        """);

    [Fact]
    public void ResolveLayout_DeclaredPartitions_DerivesOffsetsByAccumulatingDeclaredSizes()
    {
        var path = WritePackage(
            [
                new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
                new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
                new PackageEntry("XML", "scheme.xml", PartitionTableSchemeXml()),
                new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
                new PackageEntry("Super", "super.img", PackagePayload(24)),
                new PackageEntry("UserData", "userdata.img", PackagePayload(8)),
            ],
            "ums512_1h10");

        using var package = PacImage.Open(path);
        var layout = SprdPacLoaders.ResolveLayout(package);

        Assert.Equal(["prodnv", "miscdata", "super", "userdata"],
            layout.Partitions.Select(partition => partition.id).ToArray());
        Assert.Equal(0L, layout.Partitions[0].offset);
        Assert.Equal(10L * 1024 * 1024, layout.Partitions[1].offset);
        Assert.Equal(11L * 1024 * 1024, layout.Partitions[2].offset);
        Assert.Equal(4100L * 1024 * 1024, layout.Partitions[2].size);
        Assert.True(layout.Partitions[3].fillsRemainingSpace);
        Assert.Null(layout.Partitions[3].offset);
        Assert.Equal(0L, layout.Partitions[3].size);
    }

    [Fact]
    public void ResolveLayout_MissingPartitionUnit_UsesMegabytePerVendorPac()
    {
        var path = WritePackage(
            [
                new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
                new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
                new PackageEntry("XML", "scheme.xml", PartitionTableXmlWithoutUnit()),
                new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
            ],
            "ums512_1h10");

        using var package = PacImage.Open(path);
        var layout = SprdPacLoaders.ResolveLayout(package);

        Assert.Equal(0L, layout.Partitions[0].offset);
        Assert.Equal(5L * 1024 * 1024, layout.Partitions[1].offset);
        Assert.Equal(5L * 1024 * 1024, layout.Partitions[0].size);
    }

    [Fact]
    public void ResolveLayout_InvalidPartitionUnit_UsesMegabytePerVendorPac()
    {
        var path = WritePackage(
            [
                new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
                new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
                new PackageEntry("XML", "scheme.xml", PartitionTableXmlWithUnit("abc")),
                new PackageEntry("BOOT", "boot.img", PackagePayload(16)),
            ],
            "ums512_1h10");

        using var package = PacImage.Open(path);
        var layout = SprdPacLoaders.ResolveLayout(package);

        Assert.Equal(5L * 1024 * 1024, layout.Partitions[1].offset);
    }

    private static byte[] PartitionTableXmlWithoutUnit() => PartitionTableXmlWithUnit(null);

    private static byte[] PartitionTableXmlWithUnit(string? unit) => Encoding.ASCII.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <BMAConfig>
          <ProductList>
            <Product name="ums512_1h10">
              <SchemeName>ums512_1h10</SchemeName>
              <FlashTypeID>1</FlashTypeID>
              <Partitions{(unit is null ? string.Empty : $" unit=\"{unit}\"")}>
                <Partition id="one" size="5"/>
                <Partition id="two" size="7"/>
              </Partitions>
            </Product>
          </ProductList>
          <SchemeList>
            <Scheme name="ums512_1h10">
              <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
            </Scheme>
          </SchemeList>
        </BMAConfig>
        """);

    [Fact]
    public void ResolveLayout_NoPartitionTable_ReportsNoPartitions()
    {
        var path = WritePackage([
            new PackageEntry("FDL", "fdl1.bin", PackagePayload(4)),
            new PackageEntry("FDL2", "fdl2.bin", PackagePayload(4)),
            new PackageEntry("XML", "scheme.xml", SchemeXml("0x00005500", "0x9EFFFE00", "boot")),
            new PackageEntry("boot", "boot.img", PackagePayload(16)),
        ]);

        using var package = PacImage.Open(path);
        var layout = SprdPacLoaders.ResolveLayout(package);

        Assert.Empty(layout.Partitions);
    }

    private static byte[] PartitionTableSchemeXml() => Encoding.ASCII.GetBytes("""
        <?xml version="1.0" encoding="UTF-8"?>
        <BMAConfig>
          <ProductList>
            <Product name="ums512_1h10">
              <SchemeName>ums512_1h10</SchemeName>
              <FlashTypeID>1</FlashTypeID>
              <Partitions unit="0">
                <Partition id="prodnv" size="10"/>
                <Partition id="miscdata" size="1"/>
                <Partition id="super" size="4100"/>
                <Partition id="userdata" size="0xFFFFFFFF"/>
              </Partitions>
            </Product>
          </ProductList>
          <SchemeList>
            <Scheme name="ums512_1h10">
              <File><ID>FDL</ID><Block><Base>0x00005500</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>FDL2</ID><Block><Base>0x9EFFFE00</Base></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>BOOT</ID><IDAlias>BOOT</IDAlias><Type>CODE2</Type><Block id="boot"><Base>0x0</Base><Size>0x0</Size></Block><Flag>1</Flag><CheckFlag>1</CheckFlag></File>
              <File><ID>Super</ID><IDAlias>Super</IDAlias><Type>YAFFS_IMG2</Type><Block id="super"><Base>0x0</Base><Size>0x0</Size></Block><Flag>1</Flag><CheckFlag>2</CheckFlag></File>
              <File><ID>UserData</ID><IDAlias>UserData</IDAlias><Type>YAFFS_IMG2</Type><Block id="userdata"><Base>0x0</Base><Size>0x0</Size></Block><Flag>1</Flag><CheckFlag>2</CheckFlag></File>
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

    private string WritePackage(IReadOnlyList<PackageEntry> entries, string? productName = null)
    {
        Directory.CreateDirectory(TemporaryDirectory);
        var path = Path.Combine(TemporaryDirectory, $"package-{Guid.NewGuid():N}.pac");
        var headerSize = 2124;
        var entrySize = 2580;
        var payloadOffset = headerSize + entries.Count * entrySize;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
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

        WritePacCrcs(stream, header, payloadOffset);
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
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
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

        WritePacCrcs(stream, header, payloadOffset);
        _files.Add(path);
        return path;
    }

    private static void WritePacCrcs(FileStream stream, byte[] header, int payloadOffset)
    {
        var headCrc = Crc16Arc.Compute(header.AsSpan(0, 2120), 0);
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48));
        var dataSize = declaredSize - 2124u;
        stream.Position = 2124;
        var buffer = new byte[1024 * 1024];
        ushort dataCrc = 0;
        long remaining = dataSize;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, chunk);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            dataCrc = Crc16Arc.Compute(buffer.AsSpan(0, read), dataCrc);
            remaining -= read;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2120), headCrc);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2122), dataCrc);
        stream.Position = 2120;
        stream.Write(header, 2120, 4);
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
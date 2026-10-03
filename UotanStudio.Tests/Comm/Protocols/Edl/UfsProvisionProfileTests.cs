using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class UfsProvisionProfileTests
{
    private const string CompleteProfile = """
        <data>
          <ufs type="common" bNumberLU="8" bBootEnable="1" bDescrAccessEn="1" bInitPowerMode="2"
               bHighPriorityLUN="1" bSecureRemovalType="0" bInitActiveICCLevel="3"
               wPeriodicRTCUpdate="4096" bConfigDescrLock="1" />
          <ufs type="body" LUNum="0" bLUEnable="1" bBootLunID="1" size_in_kb="4096"
               bDataReliability="0" bLUWriteProtect="0" bMemoryType="1" bLogicalBlockSize="0"
               bProvisioningType="1" wContextCapabilities="2" desc="boot-a" />
          <ufs type="body" LUNum="1" bLUEnable="1" bBootLunID="0" size_in_kb="2097152"
               bDataReliability="1" bLUWriteProtect="0" bMemoryType="0" bLogicalBlockSize="0"
               bProvisioningType="1" wContextCapabilities="0" />
          <ufs type="epilogue" LUNtoGrow="0" commit="1" />
        </data>
        """;

    private const string MinimalProfile = """
        <data>
          <ufs type="common" bNumberLU="2" />
          <ufs type="body" LUNum="0" size_in_kb="4" />
          <ufs type="body" LUNum="1" size_in_kb="8" />
          <ufs type="epilogue" commit="0" />
        </data>
        """;

    [Fact]
    public async Task LoadAsync_CompleteProfile_MapsEveryDescriptorField()
    {
        var profile = await LoadAsync(CompleteProfile);

        Assert.Equal(8, profile.Common.BNumberLU);
        Assert.Equal(1, profile.Common.BBootEnable);
        Assert.Equal(1, profile.Common.BDescrAccessEn);
        Assert.Equal(2, profile.Common.BInitPowerMode);
        Assert.Equal(1, profile.Common.BHighPriorityLUN);
        Assert.Equal(0, profile.Common.BSecureRemovalType);
        Assert.Equal(3, profile.Common.BInitActiveICCLevel);
        Assert.Equal(4096, profile.Common.WPeriodicRTCUpdate);
        Assert.Equal(1, profile.Common.BConfigDescrLock);
        Assert.Equal(2, profile.Bodies.Count);
        Assert.Equal(0, profile.Bodies[0].LUNum);
        Assert.Equal(1, profile.Bodies[0].BLUEnable);
        Assert.Equal(1, profile.Bodies[0].BBootLUNID);
        Assert.Equal(4096, profile.Bodies[0].SizeInKb);
        Assert.Equal(0, profile.Bodies[0].BDataReliability);
        Assert.Equal(0, profile.Bodies[0].BLUWriteProtect);
        Assert.Equal(1, profile.Bodies[0].BMemoryType);
        Assert.Equal(0, profile.Bodies[0].BLogicalBlockSize);
        Assert.Equal(1, profile.Bodies[0].BProvisioningType);
        Assert.Equal(2, profile.Bodies[0].WContextCapacities);
        Assert.Equal("boot-a", profile.Bodies[0].Desc);
        Assert.Equal(1, profile.Bodies[1].BDataReliability);
        Assert.Equal(2097152, profile.Bodies[1].SizeInKb);
        Assert.Null(profile.Bodies[1].Desc);
        Assert.Equal(0, profile.Epilogue.LUNumToGrow);
        Assert.True(profile.Commit);
        Assert.Equal(2101248, profile.TotalSizeInKb);
    }

    [Fact]
    public async Task LoadAsync_OmittedOptionalAttributes_DefaultsToZeroAndPendingCommit()
    {
        var profile = await LoadAsync(MinimalProfile);

        Assert.Equal(2, profile.Common.BNumberLU);
        Assert.Equal(0, profile.Common.BBootEnable);
        Assert.Equal(0, profile.Common.BConfigDescrLock);
        Assert.Equal(4, profile.Bodies[0].SizeInKb);
        Assert.Equal(8, profile.Bodies[1].SizeInKb);
        Assert.Null(profile.Bodies[0].Desc);
        Assert.Equal(0, profile.Epilogue.LUNumToGrow);
        Assert.False(profile.Commit);
        Assert.Equal(12, profile.TotalSizeInKb);
    }

    [Theory]
    [InlineData("common", "bNumberLU", "BNumberLU")]
    [InlineData("body", "bLUEnable", "BLUEnable")]
    [InlineData("body", "bBootLunID", "BBootLUNID")]
    [InlineData("body", "wContextCapabilities", "WContextCapacities")]
    [InlineData("epilogue", "LUNtoGrow", "LUNumToGrow")]
    public async Task LoadAsync_CanonicalAndLegacySpellings_BindToTheSameDescriptorField(string type,
        string canonical, string legacy)
    {
        foreach (var spelling in new[] { canonical, legacy, canonical.ToUpperInvariant() })
        {
            var profile = await LoadAsync(DocumentWith(type, spelling));
            Assert.Single(profile.Bodies);

            var value = type switch
            {
                "common" => profile.Common.BNumberLU,
                "epilogue" => profile.Epilogue.LUNumToGrow,
                _ when canonical == "wContextCapabilities" => profile.Bodies[0].WContextCapacities,
                _ when canonical == "bBootLunID" => profile.Bodies[0].BBootLUNID,
                _ => profile.Bodies[0].BLUEnable
            };
            Assert.Equal(3, value);
        }
    }

    [Theory]
    [InlineData("empty", "no commands")]
    [InlineData("bare-element", "carries attributes")]
    [InlineData("unknown-type", "unsupported type")]
    [InlineData("missing-type", "unsupported type")]
    [InlineData("out-of-order", "must start with one common command")]
    [InlineData("missing-epilogue", "must start with one common command")]
    [InlineData("body-after-epilogue", "one command per LUN")]
    [InlineData("two-commons", "must start with one common command")]
    [InlineData("two-epilogues", "one command per LUN")]
    [InlineData("no-bodies", "declares 0 LUN bodies")]
    [InlineData("unknown-attribute", "is not a UFS provisioning descriptor field")]
    [InlineData("attribute-count", "above the 32-attribute limit")]
    [InlineData("long-description", "characters, above the 4096-character limit")]
    [InlineData("namespaced-attribute", "is namespaced")]
    [InlineData("root-attributes", "carries attributes")]
    [InlineData("foreign-element", "contains a <patch> element")]
    [InlineData("attribute-only-command", "carries no descriptor attributes")]
    [InlineData("duplicate-field", "is supplied more than once")]
    [InlineData("byte-overflow", "must be a whole number from 0 to 255")]
    [InlineData("word-overflow", "must be a whole number from 0 to 65535")]
    [InlineData("negative-size", "must be a whole number from 0 to 1073741824")]
    [InlineData("huge-size", "must be a whole number from 0 to 1073741824")]
    [InlineData("non-numeric", "must be a whole number from 0 to 255")]
    [InlineData("padded-number", "must be a whole number from 0 to 255")]
    [InlineData("commit-out-of-range", "must be a whole number from 0 to 1")]
    [InlineData("lun-window", "must be a whole number from 0 to 63")]
    [InlineData("lun-beyond-declared", "outside the 4 logical units")]
    [InlineData("duplicate-lun", "described more than once")]
    [InlineData("grow-window", "must be a whole number from 0 to 255")]
    [InlineData("pinned-slot", "pins a slot")]
    [InlineData("description-too-long", "above the 4096-character limit")]
    [InlineData("too-many-commands", "above the 66-command limit")]
    [InlineData("malformed-xml", "could not be read")]
    [InlineData("nested-commands", "could not be read")]
    public async Task LoadAsync_UntrustedDocument_RejectedWithReason(string shape, string expected)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Shape(shape)));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UfsProvisionProfileLoader.LoadAsync(stream, TestContext.Current.CancellationToken));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MaximalLogicalUnitTable_IsAccepted()
    {
        var bodies = string.Concat(Enumerable.Range(0, UfsProvisionProfile.MaximumLunCount)
            .Select(index => $"<ufs type='body' LUNum='{index}' size_in_kb='4096' />"));
        var profile = await LoadAsync("<data><ufs type='common' bBootEnable='1' />" + bodies +
            "<ufs type='epilogue' commit='0' /></data>");

        Assert.Equal(UfsProvisionProfile.MaximumLunCount, profile.Bodies.Count);
        Assert.Equal(4096L * UfsProvisionProfile.MaximumLunCount, profile.TotalSizeInKb);
    }

    [Fact]
    public async Task LoadAsync_DocumentLargerThanTheInputLimit_ReadsOnlyTheBudget()
    {
        var padding = new string('x', UfsProvisionProfile.MaximumProfileBytes);
        await using var inner = new MemoryStream(Encoding.UTF8.GetBytes(
            $"<data><!--{padding}--><ufs type='common' bNumberLU='2' /><ufs type='body' LUNum='0' " +
            "size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>"));
        var counting = new CountingStream(inner);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UfsProvisionProfileLoader.LoadAsync(counting, TestContext.Current.CancellationToken));

        Assert.Contains("input limit", exception.Message, StringComparison.Ordinal);
        Assert.True(counting.BytesRead <= UfsProvisionProfile.MaximumProfileBytes,
            $"the loader consumed {counting.BytesRead} bytes");
    }

    [Fact]
    public async Task LoadAsync_FileLargerThanTheInputLimit_RejectedWithoutParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-ufs-profile-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(path, MinimalProfile, TestContext.Current.CancellationToken);
            using (var file = File.Open(path, FileMode.Open, FileAccess.Write))
            {
                file.SetLength(UfsProvisionProfile.MaximumProfileBytes + 1L);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                UfsProvisionProfileLoader.LoadAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("input limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-ufs-missing-{Guid.NewGuid():N}.xml");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            UfsProvisionProfileLoader.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_BlankPath_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            UfsProvisionProfileLoader.LoadAsync("  ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ReadsNothingFromTheStream()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var counting = new CountingStream(new MemoryStream(Encoding.UTF8.GetBytes(MinimalProfile)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UfsProvisionProfileLoader.LoadAsync(counting, cancellation.Token));

        Assert.Equal(0, counting.BytesRead);
    }

    [Fact]
    public async Task LoadAsync_FileRoundTrip_FramesTheSameCommandsAsTheStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-ufs-profile-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(path, CompleteProfile, TestContext.Current.CancellationToken);
            var fromFile = await UfsProvisionProfileLoader.LoadAsync(path, TestContext.Current.CancellationToken);
            var fromStream = await LoadAsync(CompleteProfile);

            Assert.Equal(fromStream.TotalSizeInKb, fromFile.TotalSizeInKb);
            Assert.Equal(fromStream.FrameCommands(0), fromFile.FrameCommands(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FrameCommands_CommittedProfile_FramesCommonBodiesThenEpilogueWithCommitOne()
    {
        var profile = new UfsProvisionProfile(
            new UfsCommon { BNumberLU = 2, BBootEnable = 1 },
            [new UfsBody { LUNum = 0, SizeInKb = 4096, Desc = "a<&b" }, new UfsBody { LUNum = 1, SizeInKb = 8192 }],
            new UfsEpilogue { LUNumToGrow = 1 },
            commit: true);

        var commands = profile.FrameCommands(2);

        Assert.Equal(4, commands.Count);
        var elements = commands
            .Select(command => XDocument.Parse(command).Root!.Element("ufs")!)
            .ToArray();
        Assert.Equal("2", (string?)elements[0].Attribute("bNumberLU"));
        Assert.Equal("0", (string?)elements[1].Attribute("LUNum"));
        Assert.Equal("4096", (string?)elements[1].Attribute("size_in_kb"));
        Assert.Equal("a<&b", (string?)elements[1].Attribute("desc"));
        Assert.Equal("1", (string?)elements[2].Attribute("LUNum"));
        Assert.Equal("1", (string?)elements[3].Attribute("LUNtoGrow"));
        Assert.Equal("1", (string?)elements[3].Attribute("commit"));
        Assert.All(elements, element => Assert.Equal("2", (string?)element.Attribute("slot")));
        Assert.Equal(FirehoseXmlBuilder.BuildUfsEpilogue(new UfsEpilogue { LUNumToGrow = 1 }, true, 2), commands[3]);
    }

    [Fact]
    public void FrameCommands_DescriptorsMutatedAfterConstruction_FramesTheStoredSnapshot()
    {
        var body = new UfsBody { LUNum = 0, SizeInKb = 4096 };
        var profile = new UfsProvisionProfile(new UfsCommon { BNumberLU = 1 }, [body], new UfsEpilogue());

        body.SizeInKb = 999_999;

        var framed = XDocument.Parse(profile.FrameCommands(0)[1]).Root!.Element("ufs")!;
        Assert.Equal("4096", (string?)framed.Attribute("size_in_kb"));
    }

    [Theory]
    [InlineData(512, 4096, true)]
    [InlineData(4096, 4096, false)]
    [InlineData(4096, 1, true)]
    [InlineData(2048, 4096, false)]
    public void ValidateAgainstGeometry_DeviceReportedBlockWidth_DecidesWhetherSizesAreAccepted(long blockBytes,
        long sizeInKb, bool rejected)
    {
        var profile = new UfsProvisionProfile(new UfsCommon { BNumberLU = 1 },
            [new UfsBody { LUNum = 0, SizeInKb = sizeInKb }], new UfsEpilogue());
        var geometry = new FirehoseStorageGeometry(0, blockBytes, 1_000_000);

        if (!rejected)
        {
            profile.ValidateAgainstGeometry(geometry);
            return;
        }

        var exception = Assert.Throws<InvalidDataException>(() => profile.ValidateAgainstGeometry(geometry));
        Assert.Contains(blockBytes < UfsProvisionProfile.MinimumUfsBlockBytes
            ? "not a UFS block width"
            : "not a whole number", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAgainstGeometry_NullGeometry_IsRejected()
    {
        var profile = new UfsProvisionProfile(new UfsCommon { BNumberLU = 1 },
            [new UfsBody { LUNum = 0, SizeInKb = 4096 }], new UfsEpilogue());

        Assert.Throws<ArgumentNullException>(() => profile.ValidateAgainstGeometry(null!));
    }

    [Theory]
    [InlineData("no-bodies", "needs one to 64 LUN bodies")]
    [InlineData("too-many-bodies", "needs one to 64 LUN bodies")]
    [InlineData("duplicate-lun", "described more than once")]
    [InlineData("negative-size", "outside the 0-1073741824 KB range")]
    [InlineData("huge-size", "outside the 0-1073741824 KB range")]
    [InlineData("long-description", "above the 4096-character limit")]
    [InlineData("lun-window", "outside the 0-63 window")]
    [InlineData("grow-window", "outside the 0-255 range")]
    [InlineData("byte-field", "does not fit an unsigned byte")]
    [InlineData("word-field", "does not fit an unsigned word")]
    public void Constructor_DescriptorsOutsideTheDocumentedBounds_RejectBeforeFraming(string failure,
        string expected)
    {
        var (common, bodies, epilogue) = Mutate(failure);

        var exception = Assert.Throws<InvalidDataException>(() => new UfsProvisionProfile(common, bodies, epilogue));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null-common")]
    [InlineData("null-bodies")]
    [InlineData("null-epilogue")]
    [InlineData("null-body-entry")]
    public void Constructor_NullDescriptorElements_RejectBeforeFraming(string failure)
    {
        var (common, bodies, epilogue) = Mutate(failure);

        Assert.Throws<ArgumentNullException>(() => new UfsProvisionProfile(common, bodies, epilogue));
    }

    private static (UfsCommon Common, List<UfsBody> Bodies, UfsEpilogue Epilogue) Mutate(string failure)
    {
        var common = new UfsCommon { BNumberLU = 2 };
        var bodies = new List<UfsBody> { new() { LUNum = 0, SizeInKb = 4096 } };
        var epilogue = new UfsEpilogue();
        switch (failure)
        {
            case "no-bodies":
                bodies.Clear();
                break;
            case "too-many-bodies":
                common.BNumberLU = 0;
                bodies = [.. Enumerable.Range(0, UfsProvisionProfile.MaximumLunCount + 1)
                    .Select(index => new UfsBody { LUNum = index, SizeInKb = 4 })];
                break;
            case "duplicate-lun":
                bodies.Add(new UfsBody { LUNum = 0, SizeInKb = 4096 });
                break;
            case "negative-size":
                bodies[0].SizeInKb = -1;
                break;
            case "huge-size":
                bodies[0].SizeInKb = UfsProvisionProfile.MaximumLunSizeInKb + 1;
                break;
            case "long-description":
                bodies[0].Desc = new string('d', UfsProvisionProfile.MaximumDescriptionLength + 1);
                break;
            case "lun-window":
                common.BNumberLU = 0;
                bodies[0].LUNum = UfsProvisionProfile.MaximumLunCount;
                break;
            case "grow-window":
                epilogue.LUNumToGrow = -1;
                break;
            case "byte-field":
                common.BBootEnable = 256;
                break;
            case "word-field":
                common.WPeriodicRTCUpdate = 65536;
                break;
            case "null-common":
                common = null!;
                break;
            case "null-bodies":
                bodies = null!;
                break;
            case "null-epilogue":
                epilogue = null!;
                break;
            case "null-body-entry":
                bodies.Add(null!);
                break;
        }

        return (common, bodies, epilogue);
    }

    [Theory]
    [InlineData(1, 1, "outside the 1 logical units")]
    [InlineData(4, 7, "outside the 4 logical units")]
    [InlineData(0, 64, "outside the 0-63 window")]
    [InlineData(0, -1, "outside the 0-63 window")]
    public void Constructor_LogicalUnitOutsideTheAcceptedWindow_Rejects(int declaredLogicalUnits, int lunum,
        string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => new UfsProvisionProfile(
            new UfsCommon { BNumberLU = declaredLogicalUnits },
            [new UfsBody { LUNum = lunum, SizeInKb = 4096 }], new UfsEpilogue()));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static string DocumentWith(string type, string name) => type switch
    {
        "common" => $"<data><ufs type='common' {name}='3' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "body" => $"<data><ufs type='common' bNumberLU='2' />" +
            $"<ufs type='body' LUNum='0' size_in_kb='4' {name}='3' />" +
            "<ufs type='epilogue' commit='0' /></data>",
        _ => $"<data><ufs type='common' bNumberLU='4' /><ufs type='body' LUNum='0' size_in_kb='4' />" +
            $"<ufs type='epilogue' {name}='3' commit='0' /></data>"
    };

    private static async Task<UfsProvisionProfile> LoadAsync(string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await UfsProvisionProfileLoader.LoadAsync(stream, TestContext.Current.CancellationToken);
    }

    private static string Shape(string shape) => shape switch
    {
        "empty" => "<data />",
        "bare-element" => "<ufs type='common' bNumberLU='2' />",
        "unknown-type" => "<data><ufs type='lun' LUNum='0' /></data>",
        "missing-type" => "<data><ufs LUNum='0' /></data>",
        "out-of-order" => "<data><ufs type='body' LUNum='0' size_in_kb='4' />" +
            "<ufs type='common' bNumberLU='2' /><ufs type='epilogue' commit='0' /></data>",
        "missing-epilogue" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /></data>",
        "body-after-epilogue" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='epilogue' commit='0' /><ufs type='body' LUNum='0' size_in_kb='4' /></data>",
        "two-commons" => "<data><ufs type='common' bNumberLU='2' /><ufs type='common' bBootEnable='1' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "two-epilogues" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' />" +
            "<ufs type='epilogue' LUNtoGrow='1' /></data>",
        "no-bodies" => "<data><ufs type='common' bNumberLU='2' /><ufs type='epilogue' commit='0' /></data>",
        "unknown-attribute" => "<data><ufs type='common' bNumberLUX='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "attribute-count" => "<data><ufs type='common' " +
            string.Concat(Enumerable.Range(0, UfsProvisionProfile.MaximumDescriptorAttributes + 1)
                .Select(index => $"x{index}='0' ")) + "/><ufs type='body' LUNum='0' size_in_kb='4' />" +
            "<ufs type='epilogue' commit='0' /></data>",
        "long-description" => "<data><ufs type='common' bNumberLU='2' /><ufs type='body' LUNum='0' " +
            "size_in_kb='4' Desc=\"" + new string('x', UfsProvisionProfile.MaximumDescriptionLength + 1) +
            "\" /><ufs type='epilogue' commit='0' /></data>",
        "namespaced-attribute" => "<data><ufs xmlns:q='urn:q' q:bNumberLU='2' type='common' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "root-attributes" => "<data version='1'><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "foreign-element" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /><patch /></data>",
        "attribute-only-command" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' /></data>",
        "duplicate-field" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' wContextCapabilities='1' " +
            "WContextCapacities='2' /><ufs type='epilogue' commit='0' /></data>",
        "byte-overflow" => "<data><ufs type='common' bBootEnable='256' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "word-overflow" => "<data><ufs type='common' wPeriodicRTCUpdate='65536' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "negative-size" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='-1' /><ufs type='epilogue' commit='0' /></data>",
        "huge-size" => "<data><ufs type='common' bNumberLU='2' />" +
            $"<ufs type='body' LUNum='0' size_in_kb='{UfsProvisionProfile.MaximumLunSizeInKb + 1}' />" +
            "<ufs type='epilogue' commit='0' /></data>",
        "non-numeric" => "<data><ufs type='common' bBootEnable='eight' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "padded-number" => "<data><ufs type='common' bBootEnable=' 8' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "commit-out-of-range" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' commit='2' /></data>",
        "lun-window" => "<data><ufs type='common' bNumberLU='200' />" +
            "<ufs type='body' LUNum='64' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "lun-beyond-declared" => "<data><ufs type='common' bNumberLU='4' />" +
            "<ufs type='body' LUNum='7' size_in_kb='4' /><ufs type='epilogue' commit='0' /></data>",
        "duplicate-lun" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='body' LUNum='0' size_in_kb='8' />" +
            "<ufs type='epilogue' commit='0' /></data>",
        "grow-window" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' /><ufs type='epilogue' LUNtoGrow='256' /></data>",
        "pinned-slot" => "<data><ufs type='common' bNumberLU='2' />" +
            "<ufs type='body' LUNum='0' size_in_kb='4' slot='1' /><ufs type='epilogue' commit='0' /></data>",
        "description-too-long" => "<data><ufs type='common' bNumberLU='2' />" +
            $"<ufs type='body' LUNum='0' size_in_kb='4' desc='{new string('d', UfsProvisionProfile.MaximumDescriptionLength + 1)}' />" +
            "<ufs type='epilogue' commit='0' /></data>",
        "too-many-commands" => "<data><ufs type='common' bNumberLU='0' />" +
            string.Concat(Enumerable.Range(0, UfsProvisionProfile.MaximumLunCount + 2)
                .Select(index => $"<ufs type='body' LUNum='{index}' size_in_kb='4' />")) +
            "<ufs type='epilogue' commit='0' /></data>",
        "malformed-xml" => "<data><ufs type='common' bNumberLU='2'",
        "nested-commands" => "<data><group><ufs type='common' bNumberLU='2' /></group></data>",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
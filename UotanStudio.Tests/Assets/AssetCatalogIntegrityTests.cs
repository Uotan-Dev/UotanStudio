using UotanStudio.Assets;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Firmware.Core.Magic.Generated;
using UotanStudio.Hash.Digests;
using Xunit;

namespace UotanStudio.Tests.Assets;

/// <summary>
/// Verifies the shipped catalog end to end: every entry of a present bundle decodes to the declared length and
/// SHA-256 through <see cref="AssetStore"/>, and each bundle keeps the composition its consumers rely on.
/// <para>端到端校验随包资源目录：存在资源包的每个条目都能经 <see cref="AssetStore"/> 解出清单声明的长度与
/// SHA-256，且每个资源包保持消费方依赖的构成。</para>
/// </summary>
public class AssetCatalogIntegrityTests
{
    [Fact]
    public void Bundles_PresentEntries_DecodeToDeclaredLengthAndDigest()
    {
        var verified = 0;
        foreach (var bundle in AssetCatalog.Bundles)
        {
            if (!bundle.IsPresent)
            {
                continue;
            }

            foreach (var entry in bundle.Entries)
            {
                var payload = AssetStore.Read(entry);
                Assert.Equal(entry.Length, payload.LongLength);
                Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
                verified++;
            }
        }

        var expected = AssetCatalog.Bundles.Where(static bundle => bundle.IsPresent).Sum(static bundle => bundle.Entries.Count);
        Assert.Equal(expected, verified);
    }

    [Fact]
    public void OhosDevice_ShippedBundle_DeclaresFifteenDeviceLibraries()
    {
        var bundle = AssetCatalog.OhosDevice;

        Assert.True(bundle.IsPresent);
        Assert.Equal(AssetNames.Bundles.OhosDevice, bundle.Key);
        Assert.Equal(15, bundle.Entries.Count);
        var entry = bundle["libscrcpy_server0.z.so"];
        Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(AssetStore.Read(entry)));
    }

    [Fact]
    public void RockusbDriver_ShippedBundle_DeclaresCatInfAndSysFiles()
    {
        var bundle = AssetCatalog.RockusbDriver;
        string[] names = ["rockusb.cat", "rockusb.inf", "rockusb.sys"];

        Assert.True(bundle.IsPresent);
        foreach (var name in names)
        {
            var entry = bundle[name];
            var payload = AssetStore.Read(entry);
            Assert.Equal(entry.Length, payload.LongLength);
            Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
        }
    }

    [Fact]
    public void WinUsbCoinstaller_ShippedBundle_DeclaresTheTwoAmd64Coinstallers()
    {
        var bundle = AssetCatalog.WinUsbCoinstaller;
        string[] names = ["amd64__WdfCoInstaller01009.dll", "amd64__winusbcoinstaller2.dll"];

        Assert.True(bundle.IsPresent);
        Assert.Equal(2, bundle.Entries.Count);
        foreach (var name in names)
        {
            var entry = bundle[name];
            var payload = AssetStore.Read(entry);
            Assert.Equal(entry.Length, payload.LongLength);
            Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
        }
    }

    [Fact]
    public void AdbDriverBundles_DoNotDuplicateTheSharedCoinstallerPayloads()
    {
        foreach (var bundle in new[] { AssetCatalog.GoogleUsbDriver, AssetCatalog.RockchipAdbDriver })
        {
            Assert.DoesNotContain(bundle.Entries,
                static entry => entry.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Fonts_ShippedBundle_DeclaresTheVariableFace()
    {
        var bundle = AssetCatalog.Fonts;

        Assert.True(bundle.IsPresent);
        string[] faces = [AssetNames.Fonts.Variable, AssetNames.Fonts.Monospace, AssetNames.Fonts.MonospaceItalic];
        foreach (var face in faces)
        {
            var entry = bundle[face];
            var payload = AssetStore.Read(entry);
            Assert.Equal(entry.Length, payload.LongLength);
            Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
            Assert.True(payload.Length > 0x00010000, "a TrueType variable face must be a full sfnt");
            Assert.Equal(0x00010000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload));
        }
    }

    [Fact]
    public void DeliveryUpdater_ShippedBundle_DeclaresTheNativeUpdater()
    {
        var bundle = AssetCatalog.DeliveryUpdater;

        Assert.True(bundle.IsPresent);
        var entry = bundle[AssetNames.Delivery.Updater];
        var payload = AssetStore.Read(entry);
        Assert.Equal(entry.Length, payload.LongLength);
        Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
    }

    [Fact]
    public void MagicRules_LoadedEngine_ReportsTheGeneratedTopLevelRuleCount()
    {
        var bundle = AssetCatalog.MagicRules;

        Assert.True(bundle.IsPresent);
        Assert.True(bundle[AssetNames.Magic.Rules].Length > 0);

        CompiledMagicEngine.EnsureLoaded();

        Assert.Equal(CompiledMagicData.RuleCount, CompiledMagicEngine.TopLevelCount);
    }
}
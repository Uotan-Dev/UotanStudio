using System.Text.Json;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the settings hop of the EROFS rebuild search: the persisted flag defaults to off, survives the source
/// generated settings document under its documented key, and a settings document written before the key existed
/// still loads as off so an upgraded profile never starts rebuilding images three times.
/// <para>固定 EROFS 重建搜索的设置链路：持久化开关默认关闭，在源生成的设置文档里以其记录的键名往返保留，
/// 且该键出现之前写出的设置文档仍按关闭加载，使升级后的配置绝不会突然开始三次完整重建镜像。</para>
/// </summary>
public sealed class ErofsRebuildOptimizationSettingsTests
{
    [Fact]
    public void ErofsRebuildOptimization_FreshSettings_FlagStaysDisabled()
    {
        Assert.False(ErofsBuildDefaults.DefaultRebuildOptimization);
        Assert.False(new AppSettings().ErofsRebuildOptimization);
    }

    [Fact]
    public void ErofsRebuildOptimization_SettingRoundTrips_UnderTheDocumentedJsonKey()
    {
        var document = JsonSerializer.SerializeToNode(new AppSettings { ErofsRebuildOptimization = true },
            AppSettingsJsonContext.Default.AppSettings)!.AsObject();

        Assert.True(document["erofsRebuildOptimization"]!.GetValue<bool>());
        Assert.True(JsonSerializer.Deserialize(document.ToJsonString(),
            AppSettingsJsonContext.Default.AppSettings)!.ErofsRebuildOptimization);
    }

    [Fact]
    public void ErofsRebuildOptimization_DocumentWithoutKey_LoadsAsDisabled()
    {
        var document = JsonSerializer.SerializeToNode(new AppSettings(),
            AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        document.Remove("erofsRebuildOptimization");

        Assert.False(JsonSerializer.Deserialize(document.ToJsonString(),
            AppSettingsJsonContext.Default.AppSettings)!.ErofsRebuildOptimization);
    }

    [Fact]
    public void AppSettings_RetiredErofsLegacyCompatibilityKey_IsDiscardedInsteadOfRewritten()
    {
        var document = JsonSerializer.SerializeToNode(new AppSettings(),
            AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        document["erofsLegacyCompatibility"] = true;

        var settings = JsonSerializer.Deserialize(document.ToJsonString(),
            AppSettingsJsonContext.Default.AppSettings)!;

        Assert.DoesNotContain("erofsLegacyCompatibility", JsonSerializer.Serialize(settings,
            AppSettingsJsonContext.Default.AppSettings));
    }
}
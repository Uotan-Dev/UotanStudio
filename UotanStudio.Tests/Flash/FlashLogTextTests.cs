using System.Globalization;
using UotanStudio.Locale;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The flash services report every user-facing line through the bundled locale dictionaries: each key the services
/// reference resolves to real text in both languages, carries the placeholders the caller fills, and never comes back
/// as the key name itself.
/// <para>烧录服务把每一行面向用户的文本都走内置语言字典：服务引用的每个键在两种语言下都解析出真实文本，占位符数量与调用方
/// 填入的参数一致，且绝不会退回键名本身。</para>
/// </summary>
public sealed class FlashLogTextTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly string[] ServiceKeys =
    [
        "Flash_Service_Canceled",
        "Flash_Service_UnsupportedFlashProtocol",
        "Flash_Service_EraseFastbootOnly",
        "Flash_Service_EraseAllFastbootOnly",
        "Flash_Service_PropertyProtocol",
        "Flash_Progress_FastbootOpening",
        "Flash_Progress_Erasing",
        "Flash_Progress_EraseDone",
        "Flash_Progress_EraseAllDone",
        "Flash_Result_ErasedViaFastboot",
        "Flash_Result_EraseAll",
        "Flash_Progress_FlashDone",
        "Flash_Progress_Uploading",
        "Flash_Progress_UploadingChunk",
        "Flash_Result_FlashedViaFastboot",
        "Flash_Result_FlashedSparseChunks",
        "Flash_Progress_AdbOpening",
        "Flash_Progress_AdbDone",
        "Flash_Result_AdbFlashed",
        "Flash_Progress_SideloadReboot",
        "Flash_Progress_SideloadDone",
        "Flash_Result_Sideloaded",
        "Flash_Progress_RkdlOpening",
        "Flash_Progress_RkdlWritingTo",
        "Flash_Result_RkdlWroteLba",
        "Flash_Progress_BackupDone",
        "Flash_Progress_RestoreDone",
        "Flash_Result_BackedUpViaFastboot",
        "Flash_Result_RestoredViaFastboot",
        "Flash_Exception_NoFirehose",
        "Flash_Exception_ImageFileMissing",
        "Flash_Exception_OtaMissing",
        "Flash_Exception_RkMaskRom",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServiceKeys_ResolveToRealTextInEveryLanguage(bool chinese)
    {
        var culture = chinese ? Chinese : English;
        var service = LanguageService.Instance;
        Assert.All(ServiceKeys, key =>
        {
            var text = service.GetTranslation(key, culture);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        });
    }

    [Fact]
    public void ServiceKeys_BothLanguages_DifferWhereverTheyShouldBeTranslated()
    {
        var service = LanguageService.Instance;
        var identical = ServiceKeys
            .Where(key => service.GetTranslation(key, English) == service.GetTranslation(key, Chinese))
            .ToArray();

        // A line may legitimately be identical across languages only when it carries no prose of its own, such as a
        // partition-status pair; every line with wording must be translated.
        // 不带自有措辞的行（例如“分区：状态”这类键值对）在两种语言下可以相同；带措辞的行必须翻译。
        Assert.DoesNotContain(identical, key => !key.Contains("PartitionStatus", StringComparison.Ordinal)
            && !key.Contains("ConfigEntry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServiceKeys_CarryThePlaceholdersTheirCallersFill(bool chinese)
    {
        var culture = chinese ? Chinese : English;
        var service = LanguageService.Instance;
        Assert.True(string.Format(culture, service.GetTranslation("Flash_Result_ErasedViaFastboot", culture),
            "boot").Contains("boot", StringComparison.Ordinal));
        Assert.True(string.Format(culture, service.GetTranslation("Flash_Service_UnsupportedFlashProtocol",
            culture), "adb").Contains("adb", StringComparison.Ordinal));
        var chunk = string.Format(culture, service.GetTranslation("Flash_Progress_UploadingChunk", culture), 1, 4);
        Assert.Contains("1", chunk, StringComparison.Ordinal);
        Assert.Contains("4", chunk, StringComparison.Ordinal);
    }

    [Fact]
    public void BootChainCaption_IsOnlyTheSendingLine()
    {
        var service = LanguageService.Instance;
        var english = service.GetTranslation("Flash_SpacemiTBootChainRunning", English);
        var chinese = service.GetTranslation("Flash_SpacemiTBootChainRunning", Chinese);

        Assert.Equal("Sending the boot program", english);
        Assert.Equal("正在发送引导程序", chinese);
    }

    [Fact]
    public void BootChainCaption_DoesNotRestateTheRamOnlyDetail()
    {
        var service = LanguageService.Instance;
        var chinese = service.GetTranslation("Flash_SpacemiTBootChainRunning", Chinese);

        // The button already says what it sends; the running line must not repeat the FSBL/U-Boot staging detail.
        // 按钮本身已说明发送的对象；运行中的说明行不再复述 FSBL 与 U-Boot 的暂存细节。
        Assert.DoesNotContain("FSBL", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("U-Boot", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("内存", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("存储", chinese, StringComparison.Ordinal);
    }
}
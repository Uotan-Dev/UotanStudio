using UotanStudio.Firmware.BootSecurity.Workflow;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Direct fstab flag-editing tests. <c>FstabVerificationEditor.Patch</c> is the single place deciding which fs_mgr
/// flags survive a verity or forced-encryption disable, and it is consumed by both the security-disable workflow and
/// the boot-image ramdisk patcher. These tests pin each flag family, each option combination, the whole-token
/// matching rule that keeps look-alike flags intact, and the invariant that a record separator is never swallowed.
/// <para>fstab 标记编辑的直接测试。<c>FstabVerificationEditor.Patch</c> 是唯一决定"关闭 verity 或强制加密时哪些
/// fs_mgr 标记得以保留"的地方，并被安全关闭工作流与 boot 镜像 ramdisk 补丁器共同消费。本测试钉住每个标记族、
/// 每种选项组合、保留形似标记的整 token 匹配规则，以及"记录分隔符绝不被吞掉"这一不变量。</para>
/// </summary>
public class FstabVerificationEditorTests
{
    private const string SystemLine = "/dev/block/system /system ext4 ro,barrier=1 wait,avb=vbmeta_system\n";
    private const string DataLine = "/dev/block/userdata /data f2fs rw wait,fileencryption=aes-256-xts,forcefdeorfbe=footer\n";

    [Fact]
    public void Patch_BothDisabled_PatchesEveryLineAndKeepsRecordSeparators()
    {
        var patched = FstabVerificationEditor.Patch(SystemLine + DataLine, disableVerity: true, disableForceEncryption: true);

        Assert.Equal(
            "/dev/block/system /system ext4 ro,barrier=1 wait,\n/dev/block/userdata /data f2fs rw wait,,\n",
            patched);
    }

    [Fact]
    public void Patch_DisableVerity_RemovesAvbAndVerifyWithTheirValues()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro wait,avb=vbmeta_system,verify\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro wait,,\n", patched);
    }

    [Fact]
    public void Patch_DisableVerity_StripsAvbKeysBeforeTheGenericAvbRule()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro wait,avb_keys=/avb/vendor.avbpubkey,avb=vbmeta\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro wait,\n", patched);
    }

    [Fact]
    public void Patch_DisableVerity_RemovesTrailingAvbKeysOption()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro wait,avb=vbmeta,avb_keys=/avb/vendor.avbpubkey\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro wait,\n", patched);
    }

    [Fact]
    public void Patch_DisableVerityAlone_KeepsEncryptionFlags()
    {
        var patched = FstabVerificationEditor.Patch(DataLine, disableVerity: true, disableForceEncryption: false);

        Assert.Equal(DataLine, patched);
    }

    [Fact]
    public void Patch_DisableForceEncryption_RemovesAllThreeEncryptionFlags()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/userdata /data f2fs rw wait,forceencrypt=footer,fileencryption=aes-256-xts,forcefdeorfbe=footer\n",
            disableVerity: false, disableForceEncryption: true);

        Assert.Equal("/dev/block/userdata /data f2fs rw wait,,,\n", patched);
    }

    [Fact]
    public void Patch_DisableForceEncryptionAlone_KeepsVerityFlags()
    {
        var patched = FstabVerificationEditor.Patch(SystemLine, disableVerity: false, disableForceEncryption: true);

        Assert.Equal(SystemLine, patched);
    }

    [Fact]
    public void Patch_FlagWithoutValue_IsRemovedAsAWholeToken()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro,avb,verify wait\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro,, wait\n", patched);
    }

    [Fact]
    public void Patch_MatchingIsCaseInsensitive()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro,AVB=vbmeta,Verify,FILEENCRYPTION=aes-256-xts\n",
            disableVerity: true, disableForceEncryption: true);

        Assert.Equal("/dev/block/system /system ext4 ro,,,\n", patched);
    }

    [Fact]
    public void Patch_LeadingFlagWithoutLeadingDelimiter_IsStillRemoved()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 avb=vbmeta_system,ro\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ,ro\n", patched);
    }

    [Fact]
    public void Patch_SpaceDelimitedFlags_AreRemoved()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro avb=vbmeta verify wait\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro   wait\n", patched);
    }

    [Fact]
    public void Patch_FlagNamesInsideLargerTokens_AreNotStripped()
    {
        var text = "/dev/block/system /system ext4 ro,avbsomething=1,fileencryptionmode=x,verifyatboot\n";

        Assert.Equal(text, FstabVerificationEditor.Patch(text, disableVerity: true, disableForceEncryption: true));
    }

    [Fact]
    public void Patch_EmptyFlagValue_IsRemovedAsAWholeToken()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro,avb= wait\n",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro, wait\n", patched);
    }

    [Fact]
    public void Patch_CarriageReturnLineEndings_KeepTheirSeparators()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro wait,avb=vbmeta_system\r\n/dev/block/userdata /data f2fs rw wait,fileencryption=x\r\n",
            disableVerity: true, disableForceEncryption: true);

        Assert.Equal(
            "/dev/block/system /system ext4 ro wait,\r\n/dev/block/userdata /data f2fs rw wait,\r\n",
            patched);
    }

    [Fact]
    public void Patch_TextWithoutTrailingNewline_RemovesTrailingFlag()
    {
        var patched = FstabVerificationEditor.Patch(
            "/dev/block/system /system ext4 ro,avb",
            disableVerity: true, disableForceEncryption: false);

        Assert.Equal("/dev/block/system /system ext4 ro,", patched);
    }

    [Fact]
    public void Patch_NothingDisabled_ReturnsTextUnchanged()
    {
        var text = SystemLine + DataLine;

        Assert.Equal(text, FstabVerificationEditor.Patch(text, disableVerity: false, disableForceEncryption: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("# comment only\n")]
    [InlineData("/dev/block/system /system ext4 ro,barrier=1\n")]
    public void Patch_NoTargetFlags_ReturnsTextUnchanged(string text)
        => Assert.Equal(text, FstabVerificationEditor.Patch(text, disableVerity: true, disableForceEncryption: true));

    [Fact]
    public void Patch_MultiLineFstab_KeepsCommentAndPatchesEveryMountLine()
    {
        var patched = FstabVerificationEditor.Patch(
            "# fstab\n" + SystemLine + "/dev/block/vendor /vendor ext4 ro wait,avb\n",
            disableVerity: true, disableForceEncryption: true);

        Assert.Equal("# fstab\n/dev/block/system /system ext4 ro,barrier=1 wait,\n/dev/block/vendor /vendor ext4 ro wait,\n", patched);
    }

    [Fact]
    public void Patch_NullText_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => FstabVerificationEditor.Patch(null!, disableVerity: true, disableForceEncryption: true));
}
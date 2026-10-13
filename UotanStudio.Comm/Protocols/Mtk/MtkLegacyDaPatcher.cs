namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Applies the reference MediaTek Legacy download-agent patches that neutralize the agent's own address and
/// command checks, so a patched stage-two image can be loaded on a target whose signature checks are already
/// disabled by other means.
/// <para>应用参考实现的联发科 Legacy 下载代理补丁，用于关闭代理自身的地址与命令检查，使打过补丁的第二阶段镜像
/// 可以在签名校验已由其他方式放开的目标上加载。</para>
/// </summary>
/// <remarks>
/// <para>This is a pure byte transformation of an image the caller already holds: it searches for the two
/// documented instruction patterns and overwrites them. It does not contact a device and it does not imply
/// that a protected target will accept the result — on a locked SoC the patched image still fails the
/// signature verification, and no patch here changes that. When a patched stage two is uploaded, the digest
/// stored inside stage one must be recomputed with <see cref="MtkDaHashRepair"/> or the agent rejects the
/// upload.</para>
/// <para>这是对调用方已持有镜像的纯字节变换：只搜索两个已记录的指令模式并覆盖它们。它不联系设备，也不意味着受
/// 保护的目标会接受结果——在已锁定的 SoC 上，打过补丁的镜像仍会因签名校验失败而被拒绝，本文件的补丁不改变这一点。
/// 一旦上传打过补丁的第二阶段，就必须用 <see cref="MtkDaHashRepair"/> 重算第一阶段内保存的摘要，否则代理会拒绝该
/// 上传。</para>
/// </remarks>
public static class MtkLegacyDaPatcher
{
    private static readonly byte[] SecurityCheckPattern = [0x08, 0xB5, 0x4F, 0xF4, 0x50, 0x42];
    private static readonly byte[] SecurityCheckPatch = [0x08, 0xB5, 0x00, 0x20, 0x08, 0xBD];
    private static readonly byte[] CommandHandlerPattern = [0x30, 0xB5, 0x85, 0xB0, 0x03, 0xAB];
    private static readonly byte[] CommandHandlerPatch =
    [
        0x70, 0xB5, 0x4A, 0xF2, 0xC8, 0x64, 0xC8, 0xF2, 0x04, 0x04, 0x63, 0x6A, 0x98, 0x47, 0x63, 0x6A,
        0x05, 0x46, 0x98, 0x47, 0x06, 0x46, 0x4F, 0xF0, 0x00, 0x01, 0x28, 0x68, 0x05, 0xF1, 0x04, 0x05,
        0xA3, 0x6A, 0x98, 0x47, 0xA6, 0xF1, 0x01, 0x06, 0x00, 0x2E, 0xF6, 0xD1, 0x5A, 0x20, 0x23, 0x69,
        0xBD, 0xE8, 0x70, 0x40, 0x18, 0x47,
    ];

    /// <summary>Searches for the instruction patterns and returns the patched copy of the image.
    /// <para>搜索指令模式并返回镜像打过补丁的副本。</para></summary>
    /// <param name="stageTwo">The stage-two download agent image as it was extracted from the loader.
    /// <para>从加载器中提取出的第二阶段下载代理镜像。</para></param>
    /// <returns>The patched image plus which of the two patterns were present.
    /// <para>打过补丁的镜像，以及两个模式各是否命中。</para></returns>
    /// <exception cref="ArgumentException">A pattern match would run past the end of the image.
    /// <para>某个模式匹配会越过镜像末尾。</para></exception>
    public static MtkLegacyDaPatchResult Patch(ReadOnlySpan<byte> stageTwo)
    {
        var patched = stageTwo.ToArray();
        var security = Apply(patched, SecurityCheckPattern, SecurityCheckPatch);
        var command = Apply(patched, CommandHandlerPattern, CommandHandlerPatch);
        return new MtkLegacyDaPatchResult(patched, security, command);
    }

    /// <summary>Patches stage two and re-attests it inside stage one, the pair the agent loader uploads.
    /// <para>修补第二阶段并在第一阶段内重新担保它，即代理加载器实际上传的一对镜像。</para></summary>
    /// <remarks>
    /// <para>Nothing is returned when stage one holds no digest slot or when neither pattern matches: an
    /// unattested patch cannot be uploaded, and a patch that changes nothing must not silently rewrite the
    /// stage-one image. The uploaded stage two keeps the original signature suffix, exactly like the
    /// reference tool.</para>
    /// <para>第一阶段没有摘要槽位、或两个模式都未命中时不返回任何结果：未经担保的补丁不能上传，而不改变任何字节的补丁
    /// 也不应悄悄重写第一阶段镜像。上传用的第二阶段仍保留原始签名尾缀，与参考实现一致。</para>
    /// </remarks>
    /// <param name="stageOne">The unmodified stage-one image. <para>未被修改的第一阶段镜像。</para></param>
    /// <param name="stageTwo">The unmodified stage-two image. <para>未被修改的第二阶段镜像。</para></param>
    /// <param name="stageOneSignatureLength">Stage-one trailing signature length.
    /// <para>第一阶段尾部签名长度。</para></param>
    /// <param name="stageTwoSignatureLength">Stage-two trailing signature length.
    /// <para>第二阶段尾部签名长度。</para></param>
    /// <param name="isVersion6">Whether the loader container reports the v6 layout.
    /// <para>加载器容器是否声明 v6 布局。</para></param>
    /// <returns>The patched pair, or <see langword="null"/> when no patch can be attested.
    /// <para>打过补丁的一对镜像；无法担保补丁时返回 <see langword="null"/>。</para></returns>
    public static MtkLegacyAgentPatch? TryPatch(ReadOnlySpan<byte> stageOne, ReadOnlySpan<byte> stageTwo,
        int stageOneSignatureLength, int stageTwoSignatureLength, bool isVersion6)
    {
        if (MtkDaHashRepair.Locate(stageOne, stageTwo, stageOneSignatureLength, stageTwoSignatureLength,
                isVersion6) is not { } location)
        {
            return null;
        }

        var patched = Patch(stageTwo);
        if (!patched.IsPatched)
        {
            return null;
        }

        var uploadLength = Math.Min(location.HashedLength + stageTwoSignatureLength, stageTwo.Length);
        var uploaded = new byte[uploadLength];
        patched.Image[..location.HashedLength].CopyTo(uploaded, 0);
        stageTwo[location.HashedLength..uploadLength].CopyTo(uploaded.AsSpan(location.HashedLength));
        return new MtkLegacyAgentPatch(
            MtkDaHashRepair.Repair(stageOne, uploaded, location with { HashedLength = location.HashedLength }),
            uploaded, patched.SecurityCheckPatched, patched.CommandHandlerPatched);
    }

    private static bool Apply(byte[] image, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> replacement)
    {
        var index = image.AsSpan().IndexOf(pattern);
        if (index < 0)
        {
            return false;
        }

        if (index + replacement.Length > image.Length)
        {
            throw new ArgumentException(
                "A patch site is too close to the end of the image to hold its replacement.");
        }

        replacement.CopyTo(image.AsSpan(index));
        return true;
    }
}

/// <summary>The outcome of one Legacy stage-two patch pass.
/// <para>一次 Legacy 第二阶段补丁过程的结果。</para></summary>
/// <param name="Image">The patched copy; identical to the input when no pattern matched.
/// <para>打过补丁的副本；未命中任何模式时与输入相同。</para></param>
/// <param name="SecurityCheckPatched">Whether the address-check site was replaced.
/// <para>地址检查站点是否已被替换。</para></param>
/// <param name="CommandHandlerPatched">Whether the command handler stub was replaced.
/// <para>命令处理桩是否已被替换。</para></param>
public readonly record struct MtkLegacyDaPatchResult(byte[] Image, bool SecurityCheckPatched,
    bool CommandHandlerPatched)
{
    /// <summary>Gets whether either site was patched.
    /// <para>获取是否至少命中了一处站点。</para></summary>
    /// <value><see langword="true"/> when a site was replaced. <para>有站点被替换时为 <see langword="true"/>。</para></value>
    public bool IsPatched => SecurityCheckPatched || CommandHandlerPatched;
}
/// <summary>A patched Legacy download-agent pair that stage one attests.
/// <para>经补丁、并由第一阶段担保的 Legacy 下载代理镜像对。</para></summary>
/// <param name="StageOne">Stage one with its digest slot rewritten.
/// <para>已重写摘要槽位的第一阶段。</para></param>
/// <param name="StageTwo">The patched stage-two image to upload.
/// <para>待上传的、打过补丁的第二阶段镜像。</para></param>
/// <param name="SecurityCheckPatched">Whether the address-check site was replaced.
/// <para>地址检查站点是否已被替换。</para></param>
/// <param name="CommandHandlerPatched">Whether the command handler stub was replaced.
/// <para>命令处理桩是否已被替换。</para></param>
public sealed record MtkLegacyAgentPatch(byte[] StageOne, byte[] StageTwo, bool SecurityCheckPatched,
    bool CommandHandlerPatched);
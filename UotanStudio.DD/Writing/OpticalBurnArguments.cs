namespace UotanStudio.DD.Writing;

/// <summary>
/// Builds the argument vector for the platform optical recording helpers.
/// <para>构建平台光盘刻录助手的参数列表。</para>
/// </summary>
internal static class OpticalBurnArguments
{
    /// <summary>
    /// Builds <c>xorriso</c> arguments that burn an existing ISO image to a Linux optical device.
    /// <para>构建把已有 ISO 镜像刻录到 Linux 光驱的 <c>xorriso</c> 参数。</para>
    /// </summary>
    /// <param name="devicePath">Device node such as <c>/dev/sr0</c>.<para>设备节点，例如 <c>/dev/sr0</c>。</para></param>
    /// <param name="imagePath">Absolute ISO path.<para>ISO 绝对路径。</para></param>
    /// <param name="closeSession">Whether to close the disc session.<para>是否关闭光盘会话。</para></param>
    /// <returns>The complete argument list.<para>完整参数列表。</para></returns>
    internal static IReadOnlyList<string> Xorriso(string devicePath, string imagePath, bool closeSession)
    {
        var args = new List<string>
        {
            "-nodisk_at_status",
            "-outdev", devicePath,
            "-blank", "as_needed",
            "-map", imagePath + " /",
            "-chmod", "r--r--r-- /",
        };
        if (closeSession)
        {
            args.Add("-close");
            args.Add("on");
        }

        args.Add("-commit");
        return args;
    }

    /// <summary>
    /// Builds <c>drutil burn</c> arguments that record an image on macOS.
    /// <para>构建在 macOS 上刻录镜像的 <c>drutil burn</c> 参数。</para>
    /// </summary>
    /// <param name="deviceIndex">The recorder index reported by <c>drutil list</c>.
    /// <para><c>drutil list</c> 上报的刻录机序号。</para></param>
    /// <param name="imagePath">Absolute ISO path.<para>ISO 绝对路径。</para></param>
    /// <param name="closeSession">Whether to close the disc session.
    /// <para>是否关闭光盘会话；<c>drutil burn</c> 总是结束会话，此参数仅用于文档完整性。</para></param>
    /// <returns>The complete argument list.<para>完整参数列表。</para></returns>
    internal static IReadOnlyList<string> DrutilBurn(int deviceIndex, string imagePath, bool closeSession)
    {
        _ = closeSession;
        return
        [
            "burn",
            "-device", deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-noverify",
            "-eject",
            imagePath,
        ];
    }
}
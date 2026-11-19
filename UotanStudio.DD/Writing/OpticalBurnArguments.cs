namespace UotanStudio.DD.Writing;

/// <summary>
/// Builds the argument vector for the platform optical recording helpers.
/// <para>构建平台光盘刻录助手的参数列表。</para>
/// </summary>
internal static class OpticalBurnArguments
{
    /// <summary>
    /// Builds <c>xorriso -as cdrecord</c> arguments that burn an existing ISO image to a Linux optical device.
    /// <para>构建以 <c>xorriso -as cdrecord</c> 把已有 ISO 镜像刻录到 Linux 光驱的参数。</para>
    /// </summary>
    /// <param name="devicePath">Device node such as <c>/dev/sr0</c>.<para>设备节点，例如 <c>/dev/sr0</c>。</para></param>
    /// <param name="imagePath">Absolute ISO path.<para>ISO 绝对路径。</para></param>
    /// <param name="closeSession">
    /// Whether to close the disc session; an open session adds <c>-multi</c>.
    /// <para>是否关闭光盘会话；会话保持打开时追加 <c>-multi</c>。</para>
    /// </param>
    /// <returns>The complete argument list.<para>完整参数列表。</para></returns>
    internal static IReadOnlyList<string> Xorriso(string devicePath, string imagePath, bool closeSession)
    {
        var args = new List<string>
        {
            "-as",
            "cdrecord",
            "-v",
            "-eject",
            "dev=" + devicePath,
            "blank=as_needed",
        };
        if (!closeSession)
        {
            args.Add("-multi");
        }

        args.Add(imagePath);
        return args;
    }

    /// <summary>
    /// Builds <c>drutil burn</c> arguments that record an image on macOS. <c>-drive</c> must precede the verb,
    /// takes the one-based ordinal exactly as <c>drutil list -xml</c> reported it, and <c>-device</c> does not exist.
    /// <para>构建在 macOS 上刻录镜像的 <c>drutil burn</c> 参数。<c>-drive</c> 必须写在动词之前，取
    /// <c>drutil list -xml</c> 原样上报的一基序号，且不存在 <c>-device</c>。</para>
    /// </summary>
    /// <param name="deviceIndex">The one-based recorder ordinal reported by <c>drutil list</c>.
    /// <para><c>drutil list</c> 上报的一基刻录机序号。</para></param>
    /// <param name="imagePath">Absolute ISO path.<para>ISO 绝对路径。</para></param>
    /// <param name="closeSession">
    /// Whether to close the disc session; an open session adds <c>-appendable</c>.
    /// <para>是否关闭光盘会话；会话保持打开时追加 <c>-appendable</c>。</para>
    /// </param>
    /// <returns>The complete argument list.<para>完整参数列表。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="deviceIndex"/> is not a one-based ordinal.
    /// <para><paramref name="deviceIndex"/> 不是一基序号。</para>
    /// </exception>
    internal static IReadOnlyList<string> DrutilBurn(int deviceIndex, string imagePath, bool closeSession)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deviceIndex, 1);
        var args = new List<string>
        {
            "-drive",
            deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "burn",
        };
        if (!closeSession)
        {
            args.Add("-appendable");
        }

        args.Add(imagePath);
        return args;
    }
}
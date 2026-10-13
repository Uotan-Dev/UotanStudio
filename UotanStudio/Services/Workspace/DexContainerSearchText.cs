using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// Generates the merged multi-DEX disassembly text for JAR/APK containers.
/// <para>为 JAR/APK 容器生成多 DEX 的合并反汇编文本。</para>
/// </summary>
internal static class DexContainerSearchText
{
    /// <summary>
    /// Builds the merged disassembly text in entry order, handing each parsed DEX model to an optional callback.
    /// <para>按条目顺序生成合并反汇编文本，并把每个解析出的 DEX 模型交给可选回调。</para>
    /// </summary>
    /// <param name="containerPath">The container file path. <para>容器文件路径。</para></param>
    /// <param name="onEntry">Callback invoked after each DEX is parsed; <c>null</c> skips it. <para>每条 DEX 解析完成后的回调；为 <c>null</c> 时跳过。</para></param>
    /// <returns>The entry-sectioned merged disassembly text. <para>按条目分节的合并反汇编文本。</para></returns>
    internal static string BuildMergedSource(string containerPath, Action<DexFile>? onEntry = null)
    {
        var entries = JarDexContainer.ListDexEntries(containerPath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException("容器内没有 classes*.dex 条目。/ The container has no classes*.dex entry.");
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var dex = DexFile.Load(JarDexContainer.LoadEntry(containerPath, entry));
            builder.AppendLine($"## ===== {entry} =====");
            builder.AppendLine(Disassembler.DisassembleAll(dex));
            builder.AppendLine();
            onEntry?.Invoke(dex);
        }

        return builder.ToString();
    }
}
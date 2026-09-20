using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Containers;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>JAR/APK 容器多 DEX 合并反汇编文本的共享生成器。DEX 编辑器的合并视图与项目范围格式适配
/// 共用同一生成逻辑，保证同一容器两次文本化结果一致（项目范围导航依赖该可复现性）。</para>
/// The shared generator of the merged multi-DEX disassembly text for JAR/APK containers. The DEX
/// editor's merged view and the project-scope format adapter share this logic so the same container
/// always yields identical text (project-scope navigation relies on that reproducibility).
/// </summary>
internal static class DexContainerSearchText
{
    /// <summary>
    /// <para>按条目顺序合并生成反汇编文本；可选回调按条目回调解析后的 DEX 模型（供统计聚合）。</para>
    /// Builds the merged disassembly text in entry order; an optional callback receives each parsed
    /// DEX model (for statistic aggregation).
    /// </summary>
    /// <param name="containerPath">容器文件路径。<para>The container file path.</para></param>
    /// <param name="onEntry">每条 DEX 解析完成后的回调。<para>Callback invoked after each DEX is parsed.</para></param>
    /// <returns>按条目分节的合并反汇编文本。<para>The entry-sectioned merged disassembly text.</para></returns>
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
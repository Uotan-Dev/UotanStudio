using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>Projects comparison data into aligned rows without reading files or owning document state.</summary>
internal static class ComparisonRowBuilder
{
    /// <summary>Builds the existing single-side directory presentation in input order.</summary>
    internal static List<CompareRowViewModel> ForDirectory(IReadOnlyList<FolderCompareItem> entries) =>
        entries.Select(entry => new CompareRowViewModel(
            FolderItemStatus.LeftOnly, entry.IsDirectory,
            entry.RelativePath.Count(static c => c == '/'), string.Empty,
            Path.GetFileName(entry.RelativePath), entry.IsDirectory ? "-" : FormatSize(entry.LeftSize), true,
            string.Empty, string.Empty, string.Empty, false)).ToList();

    /// <summary>Builds left-only rows with one-based line numbers and blank right cells.</summary>
    internal static List<CompareRowViewModel> ForText(IReadOnlyList<string> lines) =>
        lines.Select((line, index) => new CompareRowViewModel(
            FolderItemStatus.LeftOnly, false, 0, (index + 1).ToString(), line, string.Empty, true,
            string.Empty, string.Empty, string.Empty, false)).ToList();

    /// <summary>
    /// <para>由两侧元数据构建元数据视图行：格式名行、摘要行、键值对行（键并集）、条目行与组件行；
    /// 条目与组件按**名称**对齐（两侧名称并集，同名条目落在同一水平行；仅单侧条目在缺失侧
    /// 置空占位，不显示名称），键值对缺失侧取 "-"；**同名但值不同也判定为差异**——键值对值不同、
    /// 组件大小不同均标记为 Different 并整行差异高亮，仅单侧存在的键/组件标记为 LeftOnly/RightOnly；
    /// 两侧都无法识别时给出单条"无法识别"提示行。</para>
    /// Builds the metadata-view rows from both sides' metadata: a format-name row, summary rows,
    /// key-value rows (key union), entry rows and component rows. Entries and components are aligned
    /// **by name** (union of both sides' names; same-name items land on the same horizontal row, and
    /// single-side items leave the missing side blank), while key-values show "-" on the missing side.
    /// **Same-name items with different values also count as differences**: differing key-value pairs
    /// and differing component sizes are marked Different with a full-row diff highlight, and keys or
    /// components present on only one side are marked LeftOnly/RightOnly; a single "unrecognized" hint
    /// row is produced when neither side is recognized.
    /// </summary>
    internal static List<CompareRowViewModel> ForMetadata(ComparisonMetadata? left, ComparisonMetadata? right, string unrecognized)
    {
        var rows = new List<CompareRowViewModel>();
        var leftRecognized = left is { FormatName.Length: > 0 };
        var rightRecognized = right is { FormatName.Length: > 0 };

        if (!leftRecognized && !rightRecognized)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 0,
                string.Empty, unrecognized, string.Empty, false,
                string.Empty, right is null ? string.Empty : unrecognized, string.Empty, false));
            return rows;
        }

        // 格式名行。 Format-name row.
        rows.Add(new CompareRowViewModel(
            FolderItemStatus.Equal, false, 0,
            string.Empty, leftRecognized ? left!.FormatName : "-", string.Empty, false,
            string.Empty, rightRecognized ? right!.FormatName : right is null ? string.Empty : "-", string.Empty, false));

        // 摘要行（按行拆分）。 Summary rows (split by line).
        var leftSummary = leftRecognized ? SplitLines(left!.Summary) : [];
        var rightSummary = rightRecognized ? SplitLines(right!.Summary) : [];
        var summaryCount = Math.Max(leftSummary.Count, rightSummary.Count);
        for (var i = 0; i < summaryCount; i++)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 1,
                string.Empty, i < leftSummary.Count ? leftSummary[i] : "-", string.Empty, false,
                string.Empty, i < rightSummary.Count ? rightSummary[i] : right is null ? string.Empty : "-", string.Empty, false));
        }

        // 元数据键值对（左右并排，取两字典键并集）。 Metadata key-values (side by side, key union).
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (leftRecognized)
        {
            foreach (var key in left!.Metadata.Keys)
            {
                keys.Add(key);
            }
        }

        if (rightRecognized)
        {
            foreach (var key in right!.Metadata.Keys)
            {
                keys.Add(key);
            }
        }

        foreach (var key in keys)
        {
            var leftValue = leftRecognized && left!.Metadata.TryGetValue(key, out var lv) ? lv : null;
            var rightValue = rightRecognized && right!.Metadata.TryGetValue(key, out var rv) ? rv : null;
            var hasLeft = leftValue is not null;
            var hasRight = rightValue is not null;
            var valueDiffers = hasLeft && hasRight
                && !string.Equals(leftValue, rightValue, StringComparison.Ordinal);

            // 同名键值对：值不同算差异（整行高亮）；仅单侧存在算单侧条目。
            // Same-name key-values: differing values count as a difference (full-row highlight);
            // keys present on only one side are single-side rows.
            var status = valueDiffers ? FolderItemStatus.Different
                : hasLeft && hasRight ? FolderItemStatus.Equal
                : hasLeft ? FolderItemStatus.LeftOnly
                : FolderItemStatus.RightOnly;

            rows.Add(new CompareRowViewModel(
                status, false, 1,
                string.Empty, key, hasLeft ? leftValue! : "-", valueDiffers,
                string.Empty, right is null ? string.Empty : key, hasRight ? rightValue! : right is null ? string.Empty : "-", valueDiffers));
        }

        // 条目行（按名称对齐：两侧名称并集，同名条目同一水平行；仅单侧条目在缺失侧置空占位，
        // 不显示名称——与文件夹模式一致）。 Entry rows (aligned by name: union of both sides'
        // names, same-name entries share a row; single-side entries leave the missing side blank,
        // matching the folder-mode behavior).
        var leftEntries = leftRecognized ? left!.Entries : [];
        var rightEntries = rightRecognized ? right!.Entries : [];
        var entryNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in leftEntries)
        {
            entryNames.Add(entry);
        }

        foreach (var entry in rightEntries)
        {
            entryNames.Add(entry);
        }

        foreach (var name in entryNames)
        {
            var hasLeft = leftEntries.Contains(name);
            var hasRight = rightEntries.Contains(name);
            rows.Add(new CompareRowViewModel(
                hasLeft && hasRight ? FolderItemStatus.Equal : hasLeft ? FolderItemStatus.LeftOnly : FolderItemStatus.RightOnly,
                false, 1,
                string.Empty, hasLeft ? name : string.Empty, string.Empty, false,
                string.Empty, hasRight ? name : string.Empty, string.Empty, false));
        }

        // 组件行（名称 + 大小，同样按名称对齐；仅单侧组件在缺失侧置空）。
        // Component rows (name + size, aligned by name too; single-side components leave the
        // missing side blank).
        var leftComponents = leftRecognized ? left!.Components : [];
        var rightComponents = rightRecognized ? right!.Components : [];
        var componentNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var component in leftComponents)
        {
            componentNames.Add(component.Name);
        }

        foreach (var component in rightComponents)
        {
            componentNames.Add(component.Name);
        }

        foreach (var name in componentNames)
        {
            var leftComponent = leftComponents.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            var rightComponent = rightComponents.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            var hasLeft = leftComponent is not null;
            var hasRight = rightComponent is not null;
            var sizeDiffers = hasLeft && hasRight && leftComponent!.SizeBytes != rightComponent!.SizeBytes;

            // 同名组件：大小不同算差异（整行高亮）；仅单侧存在算单侧条目。
            // Same-name components: differing sizes count as a difference (full-row highlight);
            // components present on only one side are single-side rows.
            var status = sizeDiffers ? FolderItemStatus.Different
                : hasLeft && hasRight ? FolderItemStatus.Equal
                : hasLeft ? FolderItemStatus.LeftOnly
                : FolderItemStatus.RightOnly;

            rows.Add(new CompareRowViewModel(
                status, false, 1,
                string.Empty, hasLeft ? leftComponent!.Name : string.Empty, hasLeft ? FormatSize(leftComponent!.SizeBytes) : string.Empty, sizeDiffers,
                string.Empty, hasRight ? rightComponent!.Name : string.Empty, hasRight ? FormatSize(rightComponent!.SizeBytes) : string.Empty, sizeDiffers));
        }

        return rows;
    }

    /// <summary>
    /// <para>按 '\n' 拆分文本并去除行尾 '\r'；空文本返回空列表。</para>
    /// Splits text by '\n' and trims trailing '\r'; an empty text yields an empty list.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            result.Add(raw.TrimEnd('\r'));
        }

        return result;
    }

    /// <summary>
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }
}

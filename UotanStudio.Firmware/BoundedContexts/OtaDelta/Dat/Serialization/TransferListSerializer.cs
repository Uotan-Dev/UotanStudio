using System.Globalization;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Models;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Serialization;

/// <summary>
/// Serializes a <see cref="TransferList"/> back to recovery transfer-list text (versions 1 through 4).
/// This is the exact inverse of <see cref="TransferListParser"/> and is validated by round-tripping through it.
/// <para>将 <see cref="TransferList"/> 序列化回 recovery 传输列表文本（版本 1 至 4）。
/// 这是 <see cref="TransferListParser"/> 的精确逆运算，通过往返解析进行验证。</para>
/// </summary>
public static class TransferListSerializer
{
    /// <summary>Writes a transfer list to a file. <para>将传输列表写入文件。</para></summary>
    /// <param name="path">
    /// Destination file path; an existing file is replaced with the rendered text.
    /// <para>目标文件路径；已有文件会被渲染后的文本覆盖。</para>
    /// </param>
    /// <param name="list">
    /// The list to render. <paramref name="list"/> must carry a version between 1 and 4.
    /// <para>待渲染的列表。<paramref name="list"/> 的版本必须在 1 到 4 之间。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is <see langword="null"/>. <para><paramref name="path"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="list"/> declares a version outside 1–4.
    /// <para><paramref name="list"/> 的版本不在 1–4 范围内。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A command cannot be rendered: its kind is unknown, a stash command has no identifier, a range set is empty
    /// or unordered, or a required SHA-1 is missing.
    /// <para>命令无法渲染：类型未知、stash 命令缺少标识、范围集为空或顺序非法，或必需的 SHA-1 缺失。</para>
    /// </exception>
    public static void Write(string path, TransferList list)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Serialize(list));
    }

    /// <summary>Renders a transfer list as recovery transfer-list text. <para>将传输列表渲染为 recovery 传输列表文本。</para></summary>
    /// <param name="list">
    /// The list to render, including its version and the commands in output order.
    /// <para>待渲染的列表，包含版本号与按输出顺序排列的命令。</para>
    /// </param>
    /// <returns>
    /// The header lines followed by one line per command, each terminated by <c>\n</c> and without a trailing
    /// blank line beyond the last command.
    /// <para>头部各行后接每条命令一行，均以 <c>\n</c> 结束，最后一条命令之后没有多余空行。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="list"/> is <see langword="null"/>. <para><paramref name="list"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="list"/> declares a version outside 1–4.
    /// <para><paramref name="list"/> 的版本不在 1–4 范围内。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A command cannot be rendered: its kind is unknown, a stash command has no identifier, a range set is empty
    /// or unordered, or a required SHA-1 is missing.
    /// <para>命令无法渲染：类型未知、stash 命令缺少标识、范围集为空或顺序非法，或必需的 SHA-1 缺失。</para>
    /// </exception>
    public static string Serialize(TransferList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Version is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(list), "Transfer-list version must be between 1 and 4.");
        }

        var builder = new StringBuilder();
        builder.Append(list.Version).Append('\n');
        builder.Append(list.NewBlocks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (list.Version >= 2)
        {
            builder.Append(list.StashEntries.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(list.MaxStashedBlocks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var command in list.Commands)
        {
            AppendCommand(builder, command, list.Version);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendCommand(StringBuilder builder, TransferListCommand command, int version)
    {
        switch (command.Kind)
        {
            case TransferListCommandKind.New:
                builder.Append("new ").Append(Ranges(command.Ranges));
                return;
            case TransferListCommandKind.Zero:
                builder.Append("zero ").Append(Ranges(command.Ranges));
                return;
            case TransferListCommandKind.Erase:
                builder.Append("erase ").Append(Ranges(command.Ranges));
                return;
            case TransferListCommandKind.Stash:
                builder.Append("stash ").Append(RequireStashId(command)).Append(' ').Append(Ranges(command.Ranges));
                return;
            case TransferListCommandKind.Free:
                builder.Append("free ").Append(RequireStashId(command));
                return;
            case TransferListCommandKind.Move:
            case TransferListCommandKind.Diff:
            case TransferListCommandKind.Imgdiff:
                AppendCopyCommand(builder, command, version);
                return;
            default:
                throw new InvalidDataException($"Unsupported transfer command kind: {command.Kind}");
        }
    }

    private static void AppendCopyCommand(StringBuilder builder, TransferListCommand command, int version)
    {
        switch (command.Kind)
        {
            case TransferListCommandKind.Diff:
                builder.Append("bsdiff ");
                break;
            case TransferListCommandKind.Imgdiff:
                builder.Append("imgdiff ");
                break;
            default:
                builder.Append("move ");
                break;
        }

        if (command.Kind is TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff)
        {
            builder.Append(Number(command.PatchOffset)).Append(' ').Append(Number(command.PatchLength)).Append(' ');
        }

        if (version >= 3)
        {
            builder.Append(Hash(command.SourceHash)).Append(' ');
            if (command.Kind != TransferListCommandKind.Move)
            {
                builder.Append(Hash(command.TargetHash)).Append(' ');
            }
            else if (command.TargetHash is not null && !string.Equals(command.SourceHash, command.TargetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Move commands mirror the source hash; distinct hashes cannot be emitted.");
            }
        }

        if (version == 1)
        {
            builder.Append(Ranges(command.SourceRanges)).Append(' ').Append(Ranges(command.Ranges));
        }
        else if (command.Kind is TransferListCommandKind.New or TransferListCommandKind.Zero or TransferListCommandKind.Erase)
        {
            builder.Append(Ranges(command.Ranges));
        }
        else
        {
            builder.Append(Ranges(command.Ranges)).Append(' ').Append(Number(command.SourceBlockCount)).Append(' ');
            builder.Append(command.SourceRanges.Count == 0 ? "-" : Ranges(command.SourceRanges));
            if (command.SourceLocations.Count > 0)
            {
                builder.Append(' ').Append(Ranges(command.SourceLocations));
            }
        }

        foreach (var (id, locations) in command.StashReferences)
        {
            builder.Append(' ').Append(id).Append(':').Append(Ranges(locations));
        }
    }

    private static string RequireStashId(TransferListCommand command) =>
        command.StashId ?? throw new InvalidDataException("Stash/free commands require a stash identifier.");

    private static string Hash(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant()
        : throw new InvalidDataException("Transfer command requires a 40-hex-digit SHA-1.");

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Ranges(IReadOnlyList<BlockRange> ranges)
    {
        if (ranges.Count == 0)
        {
            throw new InvalidDataException("Transfer command requires at least one range.");
        }

        var builder = new StringBuilder();
        builder.Append((ranges.Count * 2).ToString(CultureInfo.InvariantCulture));
        foreach (var range in ranges)
        {
            if (range.End <= range.Start)
            {
                throw new InvalidDataException("Transfer range must be non-empty and ordered.");
            }

            builder.Append(',').Append(Number(range.Start)).Append(',').Append(Number(range.End));
        }

        return builder.ToString();
    }
}
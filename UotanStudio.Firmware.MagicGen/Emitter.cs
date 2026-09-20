using System.Text;

namespace UotanStudio.Firmware.MagicGen;

/// <summary>
/// <para>把 <see cref="RuleAst"/> 序列化为运行时 <c>CompiledMagicEngine.ReadRule</c> 逐字段读取的二进制布局。</para>
/// Serializes <see cref="RuleAst"/> into the exact per-field binary layout that the runtime
/// <c>CompiledMagicEngine.ReadRule</c> reads back.
/// </summary>
internal static class Emitter
{
    public static (byte[] Blob, int TopLevelCount) Emit(List<RuleAst> tops)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
        bw.Write((uint)tops.Count);
        foreach (var r in tops)
        {
            WriteRule(bw, r);
        }

        bw.Flush();
        return (ms.ToArray(), tops.Count);
    }

    private static void WriteRule(BinaryWriter bw, RuleAst r)
    {
        bw.Write((byte)r.Kind);
        bw.Write((byte)r.NumberType);
        bw.Write((byte)r.Relation);
        bw.Write((byte)r.MaskOp);
        bw.Write((ushort)r.StringFlags);
        bw.Write((byte)r.RegexFlags);
        bw.Write((byte)r.IndirectMaskOp);
        bw.Write((byte)r.IndirectType);
        bw.Write((byte)r.PStringLengthBytes);
        bw.Write((ushort)r.Flags);
        bw.Write(r.Indent);
        bw.Write(r.Offset);
        bw.Write(r.Value);
        bw.Write(r.Mask);
        bw.Write(r.IndirectMask);
        bw.Write(r.IndirectBaseOffset);
        bw.Write(r.IndirectAddend);
        bw.Write(r.SearchRange);
        bw.Write(r.Strength);
        bw.Write(r.HasMask);
        bw.Write(r.MaskInvert);
        bw.Write(r.HasIndirectMask);
        bw.Write(r.IndirectMaskInvert);
        bw.Write(r.PStringBigEndian);
        bw.Write(r.Description ?? string.Empty);
        bw.Write(r.MimeType ?? string.Empty);
        bw.Write(r.Extensions ?? string.Empty);
        bw.Write(r.Encoding ?? string.Empty);
        bw.Write(r.Name ?? string.Empty);
        bw.Write(r.UseName ?? string.Empty);
        bw.Write(r.Source ?? string.Empty);
        bw.Write(r.RegexPattern ?? string.Empty);

        byte[] pattern = r.Pattern ?? Array.Empty<byte>();
        bw.Write(pattern.Length);
        if (pattern.Length > 0)
        {
            bw.Write(pattern);
        }

        bw.Write(r.Children.Count);
        foreach (var c in r.Children)
        {
            WriteRule(bw, c);
        }
    }
}
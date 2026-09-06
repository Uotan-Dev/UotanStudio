namespace UotanStudio.Firmware.Dex.Model;

public sealed class ProtoDefinition
{
    public int ShortyIndex { get; set; }

    public int ReturnTypeIndex { get; set; }

    public int[] ParameterTypeIndices { get; set; } = [];

    /// <summary>
    /// Computes the shorty string (e.g. "VIL") for writing.
    /// <para>计算 shorty 字符串（如 "VIL"），供写出时使用。</para>
    /// </summary>
    public string ComputeShorty(DexFile dex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ShortyChar(dex.Types[ReturnTypeIndex]));
        foreach (var p in ParameterTypeIndices)
        {
            sb.Append(ShortyChar(dex.Types[p]));
        }
        return sb.ToString();
    }

    private static char ShortyChar(string descriptor) => descriptor[0] switch
    {
        '[' => 'L',
        'L' => 'L',
        var c => c
    };
}

public sealed class FieldId
{
    public int ClassIndex { get; set; }

    public int TypeIndex { get; set; }

    public int NameIndex { get; set; }
}

public sealed class MethodId
{
    public int ClassIndex { get; set; }

    public int ProtoIndex { get; set; }

    public int NameIndex { get; set; }
}

public sealed class EncodedField
{
    public int FieldIndex { get; set; }

    public uint AccessFlags { get; set; }
}

public sealed class EncodedMethod
{
    public int MethodIndex { get; set; }

    public uint AccessFlags { get; set; }

    public CodeItem? Code { get; set; }
}

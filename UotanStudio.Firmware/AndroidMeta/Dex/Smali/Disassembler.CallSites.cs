using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class Disassembler
{
    private static void AppendCallSite(StringBuilder sb, DexFile dex, int index)
    {
        if ((uint)index >= (uint)dex.CallSites.Count)
        {
            throw new InvalidDataException("A call-site operand exceeds the call-site table.");
        }
        var values = dex.CallSites[index];
        if (values.Count < 3 || values[0].Type != EncodedValueType.MethodHandle
            || values[1].Type != EncodedValueType.String || values[2].Type != EncodedValueType.MethodType
            || (uint)values[0].AsIndex() >= (uint)dex.MethodHandles.Count
            || (uint)values[1].AsIndex() >= (uint)dex.Strings.Count
            || (uint)values[2].AsIndex() >= (uint)dex.Protos.Count)
        {
            throw new InvalidDataException("Invalid call-site bootstrap arguments.");
        }
        var handle = dex.MethodHandles[values[0].AsIndex()];
        if (handle.Kind != 4 || handle.MemberIndex >= dex.Methods.Count)
        {
            throw new InvalidDataException("A call-site bootstrap handle must reference an invoke-static method.");
        }
        sb.Append("call_site_").Append(index).Append('(');
        for (var i = 1; i < values.Count; i++)
        {
            if (i > 1)
            {
                sb.Append(", ");
            }
            sb.Append(FormatEncodedValue(dex, values[i], "    "));
        }
        sb.Append(")@").Append(FormatEncodedValue(dex, EncodedValue.Method(handle.MemberIndex), "    "));
    }
}
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.IO;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

public static partial class Disassembler
{
    private static string?[] ReadParameterNames(DexFile dex, CodeItem? code, int parameterCount)
    {
        var names = new string?[parameterCount];
        if (code?.DebugInfo is not { } bytes)
        {
            return names;
        }
        var offset = 0;
        Leb128.ReadUleb128(bytes, ref offset);
        var count = Leb128.ReadUleb128(bytes, ref offset);
        if (count > parameterCount)
        {
            throw new InvalidDataException("The debug parameter count exceeds the method signature.");
        }
        for (var i = 0; i < count; i++)
        {
            var name = Leb128.ReadUleb128(bytes, ref offset);
            if (name > dex.Strings.Count)
            {
                throw new InvalidDataException("A debug parameter name does not reference a string.");
            }
            if (name != 0)
            {
                names[i] = dex.Strings[(int)name - 1];
            }
        }
        return names;
    }
}

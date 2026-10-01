using System.Globalization;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class Disassembler
{
    private static Dictionary<int, List<string>> ReadDebugDirectives(DexFile dex, CodeItem code, IReadOnlyList<Instruction> instructions)
    {
        var result = new Dictionary<int, List<string>>();
        if (code.DebugInfo is not { } bytes)
        {
            return result;
        }
        var offset = 0;
        var line = unchecked((int)Leb128.ReadUleb128(bytes, ref offset));
        var parameterCount = Leb128.ReadUleb128(bytes, ref offset);
        if (parameterCount > bytes.Length - offset)
        {
            throw new InvalidDataException("Debug parameter count exceeds the input.");
        }
        for (var i = 0; i < parameterCount; i++)
        {
            Leb128.ReadUleb128(bytes, ref offset);
        }
        var address = 0;
        var boundaries = instructions.Select(instruction => instruction.Offset).ToHashSet();
        boundaries.Add(checked(code.RawInsns.Length * 2));
        string Register()
        {
            var register = Leb128.ReadUleb128(bytes, ref offset);
            if (register >= code.RegistersSize)
            {
                throw new InvalidDataException("A debug register exceeds the method register count.");
            }
            return "v" + register.ToString(CultureInfo.InvariantCulture);
        }
        string Reference(bool type = false)
        {
            var index = Leb128.ReadUleb128(bytes, ref offset);
            if (index > (type ? dex.Types.Count : dex.Strings.Count))
            {
                throw new InvalidDataException("A debug reference exceeds its identifier table.");
            }
            return index == 0 ? (type ? "V" : "null")
                : type ? dex.Types[(int)index - 1] : "\"" + EscapeString(dex.Strings[(int)index - 1]) + "\"";
        }
        void Add(string directive)
        {
            var byteAddress = checked(address * 2);
            if (!boundaries.Contains(byteAddress))
            {
                throw new InvalidDataException("A debug event is not at an instruction boundary.");
            }
            if (!result.TryGetValue(byteAddress, out var entries))
            {
                result.Add(byteAddress, entries = []);
            }
            entries.Add(directive);
        }
        while (offset < bytes.Length)
        {
            var op = bytes[offset++];
            switch (op)
            {
                case 0:
                    return result;
                case 1:
                    address = checked(address + (int)Leb128.ReadUleb128(bytes, ref offset));
                    break;
                case 2:
                    line = unchecked(line + Leb128.ReadSleb128(bytes, ref offset));
                    break;
                case 3:
                case 4:
                    var local = ".local " + Register() + ", " + Reference() + ":" + Reference(type: true);
                    if (op == 4)
                    {
                        var signature = Reference();
                        if (signature != "null")
                        {
                            local += ", " + signature;
                        }
                    }
                    Add(local);
                    break;
                case 5:
                    Add(".end local " + Register());
                    break;
                case 6:
                    Add(".restart local " + Register());
                    break;
                case 7:
                    Add(".prologue");
                    break;
                case 8:
                    Add(".epilogue");
                    break;
                case 9:
                    var source = Reference();
                    Add(source == "null" ? ".source" : ".source " + source);
                    break;
                default:
                    var adjusted = op - 10;
                    address = checked(address + adjusted / 15);
                    line = unchecked(line - 4 + adjusted % 15);
                    Add(".line " + line.ToString(CultureInfo.InvariantCulture));
                    break;
            }
            if (address > code.RawInsns.Length)
            {
                throw new InvalidDataException("The debug address exceeds the method body.");
            }
        }
        throw new InvalidDataException("The debug sequence has no END_SEQUENCE.");
    }
}
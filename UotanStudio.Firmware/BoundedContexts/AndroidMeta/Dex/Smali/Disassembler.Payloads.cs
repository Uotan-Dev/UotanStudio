using System.Globalization;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

public static partial class Disassembler
{
    private sealed class PayloadRenderPlan
    {
        public Dictionary<int, int> Owners { get; } = [];
        public Dictionary<int, string> TargetLabels { get; } = [];
        public List<(Instruction Payload, int Owner, string Label)> Copies { get; } = [];
    }

    private static PayloadRenderPlan CollectPayloadLabels(IReadOnlyList<Instruction> instructions, HashSet<int> labels)
    {
        var payloads = instructions.Where(instruction => instruction.Operands.FirstOrDefault() is PayloadOperand)
            .ToDictionary(instruction => instruction.Offset);
        var executable = instructions.Where(instruction => instruction.Operands.FirstOrDefault() is not PayloadOperand)
            .Select(instruction => instruction.Offset).ToHashSet();
        var plan = new PayloadRenderPlan();
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is not (0x26 or 0x2b or 0x2c))
            {
                continue;
            }
            var target = instruction.Operands.OfType<BranchTargetOperand>().SingleOrDefault();
            if (target is null || !payloads.TryGetValue(target.Offset, out var payload))
            {
                throw new InvalidDataException("A payload instruction does not reference a decoded payload.");
            }
            var kind = instruction.OpCode switch { 0x26 => "fill-array-data", 0x2b => "packed-switch", _ => "sparse-switch" };
            if (((PayloadOperand)payload.Operands[0]!).Kind != kind)
            {
                throw new InvalidDataException("A payload instruction references the wrong payload kind.");
            }
            if (kind != "fill-array-data")
            {
                if (!plan.Owners.TryAdd(target.Offset, instruction.Offset))
                {
                    var label = ":payload_for_" + instruction.Offset.ToString("X4");
                    plan.TargetLabels.Add(instruction.Offset, label);
                    plan.Copies.Add((payload, instruction.Offset, label));
                }
                foreach (var relative in SwitchTargets(payload))
                {
                    var address = checked(instruction.Offset + relative * 2);
                    if (!executable.Contains(address))
                    {
                        throw new InvalidDataException("A switch case does not target an executable instruction.");
                    }
                    labels.Add(address);
                }
            }
        }
        return plan;
    }

    private static int[] SwitchTargets(Instruction instruction)
    {
        var data = (PayloadOperand)instruction.Operands[0]!;
        if (data.Kind == "packed-switch")
        {
            return data.Values;
        }
        if (instruction.Operands.Count != 2 || instruction.Operands[1] is not PayloadOperand { Kind: "targets" } targets
            || targets.Values.Length != data.Values.Length)
        {
            throw new InvalidDataException("A sparse-switch payload requires equally sized key and target arrays.");
        }
        return targets.Values;
    }

    private static void AppendPayloadBlock(StringBuilder sb, Instruction instruction, int? owner)
    {
        var data = (PayloadOperand)instruction.Operands[0]!;
        if (data.Kind == "fill-array-data")
        {
            AppendArrayBlock(sb, data);
            return;
        }
        var targets = SwitchTargets(instruction);
        var directive = owner.HasValue ? data.Kind : data.Kind + "-raw";
        sb.Append("    .").Append(directive);
        if (data.Kind == "packed-switch")
        {
            sb.Append(' ').Append(data.FirstKey.ToString(CultureInfo.InvariantCulture));
        }
        sb.AppendLine();
        for (var i = 0; i < targets.Length; i++)
        {
            sb.Append("        ");
            if (data.Kind == "sparse-switch")
            {
                sb.Append(data.Values[i].ToString(CultureInfo.InvariantCulture)).Append(" -> ");
            }
            if (owner is { } origin)
            {
                sb.Append(":label_").Append(checked(origin + targets[i] * 2).ToString("X4")).AppendLine();
            }
            else
            {
                sb.AppendLine(targets[i].ToString(CultureInfo.InvariantCulture));
            }
        }
        sb.Append("    .end ").AppendLine(directive);
    }

    private static void AppendArrayBlock(StringBuilder sb, PayloadOperand data)
    {
        if (data.FirstKey is not (1 or 2 or 4 or 8) || data.ElementCount is not { } count
            || ((long)count * data.FirstKey + 1) / 2 != data.Values.Length)
        {
            throw new InvalidDataException("An array payload requires a valid width and an exact element count.");
        }
        var width = (int)data.FirstKey;
        sb.Append("    .array-data ").Append(width).AppendLine();
        for (long element = 0; element < count; element++)
        {
            ulong value = 0;
            for (var index = 0; index < width; index++)
            {
                var offset = element * width + index;
                var unit = data.Values[checked((int)(offset / 2))];
                value |= (ulong)((unit >> ((int)(offset % 2) * 8)) & 255) << (index * 8);
            }
            sb.Append("        0x").Append(value.ToString("x" + width * 2, CultureInfo.InvariantCulture));
            sb.Append(width switch { 1 => "t", 2 => "s", 8 => "L", _ => string.Empty }).AppendLine();
        }
        sb.AppendLine("    .end array-data");
    }
}
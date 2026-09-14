namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

public sealed record LiteralOperand(long Value);

public sealed record TypeRefOperand(string Descriptor);

public sealed record FieldRefOperand(string OwnerClass, string Name, string Type);

public sealed record MethodRefOperand(string OwnerClass, string Name, string ReturnType, string[] Parameters);

public sealed record BranchTargetOperand(int Offset);

public sealed record PayloadOperand(string Kind, long FirstKey, int[] Values);

public sealed class Instruction
{
    public int Offset { get; init; }

    public byte OpCode { get; init; }

    public string Mnemonic { get; init; } = string.Empty;

    public IReadOnlyList<object?> Operands { get; init; } = [];

    public int Size { get; init; }
}
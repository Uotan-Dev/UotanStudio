namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

public sealed class ClassDefinition
{
    public int TypeIndex { get; set; }

    public uint AccessFlags { get; set; }

    public int SuperclassIndex { get; set; } = -1;

    public int[] InterfaceTypeIndices { get; set; } = [];

    public int SourceFileIndex { get; set; } = -1;

    public List<EncodedField> StaticFields { get; } = [];

    public List<EncodedField> InstanceFields { get; } = [];

    public List<EncodedMethod> DirectMethods { get; } = [];

    public List<EncodedMethod> VirtualMethods { get; } = [];

    public IEnumerable<EncodedField> AllFields => StaticFields.Concat(InstanceFields);

    public IEnumerable<EncodedMethod> AllMethods => DirectMethods.Concat(VirtualMethods);

    public AnnotationsDirectory? Annotations { get; set; }

    public List<EncodedValue>? StaticValues { get; set; }
}
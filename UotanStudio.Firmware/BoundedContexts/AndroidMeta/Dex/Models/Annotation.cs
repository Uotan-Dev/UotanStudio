namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

public sealed class AnnotationElement
{
    public int NameIndex { get; set; }

    public EncodedValue Value { get; set; } = EncodedValue.Null();
}

public static class AnnotationVisibility
{
    public const int Build = 0x00;

    public const int Runtime = 0x01;

    public const int System = 0x02;
}

public sealed class AnnotationItem
{
    public int TypeIndex { get; set; }

    public List<AnnotationElement> Elements { get; } = [];

    public int Visibility { get; set; } = AnnotationVisibility.Build;
}

public sealed class AnnotationSet
{
    public List<AnnotationItem> Annotations { get; } = [];

    public bool IsEmpty => Annotations.Count == 0;
}

public sealed class AnnotationSetRefList
{
    public List<AnnotationSet?> Sets { get; } = [];
}

public sealed class FieldAnnotation
{
    public int FieldIndex { get; set; }

    public AnnotationSet Annotations { get; set; } = new();
}

public sealed class MethodAnnotation
{
    public int MethodIndex { get; set; }

    public AnnotationSet Annotations { get; set; } = new();
}

public sealed class ParameterAnnotation
{
    public int MethodIndex { get; set; }

    public AnnotationSetRefList Parameters { get; set; } = new();
}

public sealed class AnnotationsDirectory
{
    public AnnotationSet? ClassAnnotations { get; set; }

    public List<FieldAnnotation> FieldAnnotations { get; } = [];

    public List<MethodAnnotation> MethodAnnotations { get; } = [];

    public List<ParameterAnnotation> ParameterAnnotations { get; } = [];

    public bool IsEmpty =>
        (ClassAnnotations == null || ClassAnnotations.IsEmpty)
        && FieldAnnotations.Count == 0
        && MethodAnnotations.Count == 0
        && ParameterAnnotations.Count == 0;
}
namespace UotanStudio.Services.Flash;

/// <summary>A single validated, ordered operation in an imported flash plan.</summary>
public sealed record FlashStep(string Command, string Partition, string ImagePath = "", long ImageLength = 0);
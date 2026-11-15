using System.IO.Compression;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexRepartitionerTests
{
    [Fact]
    public void Write_ModelExceedingMethodLimit_ThrowsDexException()
    {
        var dex = CreateDex(classCount: 2, methodsPerClass: 33_000, nameOffset: 0);
        var exception = Assert.Throws<DexException>(() => dex.ToArray());
        Assert.Contains("DEX", exception.Message);
    }

    [Fact]
    public void Split_DexWithinLimits_ReturnsOriginalBytesUnchanged()
    {
        var dex = CreateDex(classCount: 3, methodsPerClass: 4, nameOffset: 0);
        var original = dex.ToArray();
        var parts = DexRepartitioner.Split(original, TestContext.Current.CancellationToken);
        Assert.Single(parts);
        Assert.Equal(original, parts[0]);
    }

    [Fact]
    public async Task Split_MethodCountOverLimit_ProducesValidPartsCoveringAllClasses()
    {
        var dex = CreateDex(classCount: 70, methodsPerClass: 1_010, nameOffset: 0);
        var descriptorSet = dex.Classes.Select(c => dex.GetType(c.TypeIndex)).ToHashSet();

        var parts = await Task.Run(() => DexRepartitioner.Split(dex, originalBytes: null, TestContext.Current.CancellationToken));
        Assert.True(parts.Count > 1);

        var seen = new HashSet<string>();
        var methodTotal = 0;
        foreach (var part in parts)
        {
            var parsed = DexFile.Load(part);
            Assert.True(parsed.Methods.Count <= DexRepartitioner.MaximumIndexedEntries);
            Assert.True(parsed.Types.Count <= DexRepartitioner.MaximumIndexedEntries);
            Assert.True(parsed.Fields.Count <= DexRepartitioner.MaximumIndexedEntries);
            Assert.True(parsed.Protos.Count <= DexRepartitioner.MaximumIndexedEntries);
            foreach (var cls in parsed.Classes)
            {
                Assert.True(seen.Add(parsed.GetType(cls.TypeIndex)), "a class must appear in exactly one part");
            }
            methodTotal += parsed.Methods.Count;
        }
        Assert.Equal(descriptorSet, seen);
        Assert.Equal(dex.Methods.Count, methodTotal);
    }

    [Fact]
    public async Task Rebalance_MultipleSmallParts_MergesIntoSinglePart()
    {
        var first = CreateDex(classCount: 5, methodsPerClass: 10, nameOffset: 0).ToArray();
        var second = CreateDex(classCount: 7, methodsPerClass: 8, nameOffset: 100).ToArray();

        var merged = await Task.Run(() => DexRepartitioner.Rebalance([first, second], TestContext.Current.CancellationToken));
        Assert.Single(merged);
        var parsed = DexFile.Load(merged[0]);
        Assert.Equal(12, parsed.Classes.Count);
    }

    [Fact]
    public async Task RewriteDexEntries_ReplacesDexSet_PreservesResourcesAndStripsSignatures()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var container = Path.Combine(temp, "sample.jar");
            var smallDex = CreateDex(classCount: 2, methodsPerClass: 3, nameOffset: 0).ToArray();
            await using (var stream = File.Create(container))
            using (var archive = ZipContainer.Create(stream))
            {
                WriteEntry(archive, "classes.dex", smallDex);
                WriteEntry(archive, "assets/config.txt", "hello"u8.ToArray());
                WriteEntry(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0"u8.ToArray());
                WriteEntry(archive, "META-INF/CERT.SF", "Signature-Version: 1.0"u8.ToArray());
            }

            var secondDex = CreateDex(classCount: 3, methodsPerClass: 5, nameOffset: 50).ToArray();
            await Task.Run(
                () => JarDexContainer.RewriteDexEntries(
                    container, [("classes.dex", smallDex), ("classes2.dex", secondDex)], stripSignatureEntries: true, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            using var verifyStream = File.OpenRead(container);
            using var verifyArchive = ZipContainer.OpenRead(verifyStream);
            var names = verifyArchive.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Equal(new HashSet<string> { "classes.dex", "classes2.dex", "assets/config.txt" }, names);
            using var dexStream = verifyArchive.Entries.Single(e => e.FullName == "classes2.dex").Open();
            using var loaded = new MemoryStream();
            dexStream.CopyTo(loaded);
            Assert.Equal(3, DexFile.Load(loaded.ToArray()).Classes.Count);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(content);
    }

    private static DexFile CreateDex(int classCount, int methodsPerClass, int nameOffset)
    {
        var dex = new DexFile("035");
        dex.Strings.Add("V");
        dex.Strings.Add("Ljava/lang/Object;");
        dex.Types.Add("V");
        dex.Types.Add("Ljava/lang/Object;");
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 0, ReturnTypeIndex = 0, ParameterTypeIndices = [] });

        for (var c = 0; c < classCount; c++)
        {
            var descriptor = $"Lcom/example/Type{nameOffset + c};";
            var descriptorIndex = dex.Strings.Count;
            dex.Strings.Add(descriptor);
            var typeIndex = dex.Types.Count;
            dex.Types.Add(descriptor);
            var definition = new ClassDefinition
            {
                TypeIndex = typeIndex,
                AccessFlags = 0x1,
                SuperclassIndex = 1
            };
            for (var m = 0; m < methodsPerClass; m++)
            {
                var nameIndex = dex.Strings.Count;
                dex.Strings.Add($"m{m}");
                var methodIndex = dex.Methods.Count;
                dex.Methods.Add(new MethodId { ClassIndex = typeIndex, ProtoIndex = 0, NameIndex = nameIndex });
                definition.DirectMethods.Add(new EncodedMethod
                {
                    MethodIndex = methodIndex,
                    AccessFlags = 0x9,
                    Code = new CodeItem { RegistersSize = 1, RawInsns = [0x000e] }
                });
            }
            dex.Classes.Add(definition);
        }
        return dex;
    }
}
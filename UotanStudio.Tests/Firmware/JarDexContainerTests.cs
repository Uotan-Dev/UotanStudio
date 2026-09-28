using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Keys;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// JAR/ZIP container DEX support tests: entry enumeration/loading/write-back round-trips,
/// resource export/replace, signature-artifact protection, routing and JAR-purpose signing.
/// <para>JAR/ZIP 容器 DEX 支持测试：条目枚举/加载/写回回环、资源导出/替换、签名工件保护、
/// 路由与 JAR 用途签名。</para>
/// </summary>
public class JarDexContainerTests : IDisposable
{
    private readonly string _root;

    public JarDexContainerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "JarDexContainerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ListEntries_ClassifiesDexSignatureAndResource()
    {
        var jar = BuildJar(["classes.dex", "classes2.dex"]);
        var entries = JarDexContainer.ListEntries(jar);

        Assert.Contains(entries, e => e.Name == "classes.dex" && e.Kind == JarEntryKind.Dex);
        Assert.Contains(entries, e => e.Name == "classes2.dex" && e.Kind == JarEntryKind.Dex);
        Assert.Contains(entries, e => e.Name == "META-INF/MANIFEST.MF" && e.Kind == JarEntryKind.Signature);
        Assert.Contains(entries, e => e.Name == "assets/config.txt" && e.Kind == JarEntryKind.Resource);
    }

    [Fact]
    public void ListDexEntries_ReturnsRootLevelClassesInOrder()
    {
        var jar = BuildJar(["classes2.dex", "classes.dex", "nested/classes.dex"]);
        var entries = JarDexContainer.ListDexEntries(jar);

        Assert.Equal(["classes.dex", "classes2.dex"], entries);
    }

    [Fact]
    public void LoadEntry_RoundTripsDexModel()
    {
        var dexBytes = BuildMinimalDex().ToArray();
        var jar = BuildJar(["classes.dex"], [dexBytes]);

        var loaded = DexFile.Load(JarDexContainer.LoadEntry(jar, "classes.dex", TestContext.Current.CancellationToken));
        Assert.Single(loaded.Classes);
    }

    [Fact]
    public void WriteBack_ReplacesOnlyTargetEntry()
    {
        var original = BuildMinimalDex().ToArray();
        var jar = BuildJar(["classes.dex", "classes2.dex"], [original, original]);

        var modified = BuildMinimalDex();
        modified.Strings.Add("modified-marker");
        JarDexContainer.WriteBack(jar, "classes.dex", modified.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(original, JarDexContainer.LoadEntry(jar, "classes2.dex", TestContext.Current.CancellationToken));
        var rebuilt = DexFile.Load(JarDexContainer.LoadEntry(jar, "classes.dex", TestContext.Current.CancellationToken));
        Assert.Equal(8, rebuilt.Strings.Count);
    }

    [Fact]
    public void WriteBack_RejectsUnknownEntry()
    {
        var jar = BuildJar(["classes.dex"]);
        Assert.Throws<InvalidDataException>(
            () => JarDexContainer.WriteBack(jar, "classes9.dex", BuildMinimalDex().ToArray(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExtractAndReplaceResource_RoundTrips()
    {
        var jar = BuildJar(["classes.dex"]);
        var output = Path.Combine(_root, "config.txt");
        JarDexContainer.ExtractResource(jar, "assets/config.txt", output, TestContext.Current.CancellationToken);
        Assert.Equal("hello", File.ReadAllText(output));

        File.WriteAllText(output, "updated");
        JarDexContainer.ReplaceResource(jar, "assets/config.txt", output, TestContext.Current.CancellationToken);
        using var stream = File.OpenRead(jar);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = archive.Entries.First(e => e.FullName == "assets/config.txt").Open();
        using var reader = new StreamReader(entry);
        Assert.Equal("updated", reader.ReadToEnd());
    }

    [Fact]
    public void SignatureEntries_AreReadOnly()
    {
        var jar = BuildJar(["classes.dex"]);
        var output = Path.Combine(_root, "manifest.mf");
        Assert.Throws<InvalidOperationException>(
            () => JarDexContainer.ExtractResource(jar, "META-INF/MANIFEST.MF", output, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(
            () => JarDexContainer.ReplaceResource(jar, "META-INF/MANIFEST.MF", output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OversizedEntry_IsRejected()
    {
        var jar = Path.Combine(_root, "oversize.jar");
        using (var stream = new FileStream(jar, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("big.bin");
            using var output = entry.Open();
            output.Write("x"u8.ToArray());
        }

        PatchCentralDirectoryUncompressedSize(jar, "big.bin", JarDexContainer.MaxEntryBytes + 1);
        Assert.Throws<InvalidDataException>(() => JarDexContainer.LoadEntry(jar, "big.bin", TestContext.Current.CancellationToken));
    }

    private static void PatchCentralDirectoryUncompressedSize(string jar, string entryName, long size)
    {
        var bytes = File.ReadAllBytes(jar);
        var eocd = -1;
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(bytes, i) == 0x06054b50) { eocd = i; break; }
        }
        Assert.True(eocd >= 0, "EOCD not found.");

        var entryCount = BitConverter.ToUInt16(bytes, eocd + 10);
        var offset = (int)BitConverter.ToUInt32(bytes, eocd + 16);
        for (var n = 0; n < entryCount; n++)
        {
            Assert.Equal(0x02014b50u, BitConverter.ToUInt32(bytes, offset));
            var nameLength = BitConverter.ToUInt16(bytes, offset + 28);
            var extraLength = BitConverter.ToUInt16(bytes, offset + 30);
            var commentLength = BitConverter.ToUInt16(bytes, offset + 32);
            var name = Encoding.ASCII.GetString(bytes, offset + 46, nameLength);
            if (name == entryName)
            {
                BitConverter.GetBytes((uint)size).CopyTo(bytes, offset + 24);
                File.WriteAllBytes(jar, bytes);
                return;
            }
            offset += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException($"Entry {entryName} not found in central directory.");
    }

    [Fact]
    public void ResolveMode_RoutesJarToDexEditor()
    {
        Assert.Equal(EditorOpenMode.Dex, FileOpenMode.ResolveMode("app.jar"));
        Assert.Equal(EditorOpenMode.Dex, FileOpenMode.ResolveMode("C:/x/APP.JAR"));
        Assert.True(FileOpenMode.IsJarExtension(".jar"));
        Assert.False(FileOpenMode.IsJarExtension(".dex"));
    }

    [Fact]
    public async Task Service_DisassembleAndRebuild_ContainerAware()
    {
        var jar = BuildJar(["classes.dex", "classes2.dex"], [BuildMinimalDex().ToArray(), BuildMinimalDex().ToArray()]);
        var service = DexService.Instance;

        var parse = await service.ParseDexAsync(jar, ct: TestContext.Current.CancellationToken);
        Assert.True(parse.Success, parse.Summary);

        var outDir = Path.Combine(_root, "smali");
        var disasm = await service.DisassembleDexAsync(jar, outDir, ct: TestContext.Current.CancellationToken);
        Assert.True(disasm.Success, disasm.Summary);
        Assert.True(File.Exists(disasm.Entries[0]), disasm.Entries[0]);
        Assert.EndsWith(".smali", disasm.Entries[0], StringComparison.OrdinalIgnoreCase);

        var rebuild = await service.RebuildDexAsync(jar, Path.Combine(_root, "unused.dex"), ct: TestContext.Current.CancellationToken);
        Assert.True(rebuild.Success, rebuild.Summary);
    }

    [Fact]
    public async Task Service_SignJar_ProducesV1Artifacts()
    {
        var jar = BuildJar(["classes.dex"], [BuildMinimalDex().ToArray()]);
        var library = new KeyLibraryService(Path.Combine(_root, "keys"));
        var key = library.Generate("sign test", ManagedKeyKind.Jar, 2048);

        var output = Path.Combine(_root, "signed.jar");
        var result = await DexService.Instance.SignJarAsync(jar, output, library.PrivatePath(key), library.EnsureCertificate(key), new UotanStudio.Firmware.AndroidMeta.Apk.Signing.ApkSigningOptions
        {
            EnableV1 = true,
            EnableV2 = false,
            EnableV3 = false,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);

        using var stream = File.OpenRead(output);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "META-INF/MANIFEST.MF");
        Assert.Contains(archive.Entries, e => e.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase));
        var dexEntry = archive.Entries.First(e => e.FullName == "classes.dex");
        using var dexStream = dexEntry.Open();
        using var memory = new MemoryStream();
        dexStream.CopyTo(memory);
        Assert.Equal(BuildMinimalDex().ToArray(), memory.ToArray());
    }

    [Fact]
    public void KeyLibrary_JarPurpose_GenerateValidateAndCertificate()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "keys2"));
        var key = library.Generate("jar key", ManagedKeyKind.Jar, 2048);

        Assert.Equal(ManagedKeyKind.Jar, key.Kind);
        Assert.True(File.Exists(library.PrivatePath(key)));
        Assert.True(File.Exists(library.CertificatePath(key)));

        Assert.Throws<ArgumentException>(() => library.Generate("bad", ManagedKeyKind.Jar, 3072));
    }

    private static string BuildJar(string[] entryNames, byte[][]? contents = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "JarDexContainerTests", Guid.NewGuid().ToString("N") + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        for (var i = 0; i < entryNames.Length; i++)
        {
            var entry = archive.CreateEntry(entryNames[i]);
            using var output = entry.Open();
            var bytes = contents is not null && i < contents.Length
                ? contents[i]
                : "hello"u8.ToArray();
            output.Write(bytes);
        }

        if (!entryNames.Contains("META-INF/MANIFEST.MF"))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using var output = manifest.Open();
            output.Write("Manifest-Version: 1.0\n"u8.ToArray());
        }

        if (!entryNames.Contains("assets/config.txt"))
        {
            var config = archive.CreateEntry("assets/config.txt");
            using var output = config.Open();
            output.Write("hello"u8.ToArray());
        }

        return path;
    }

    private static DexFile BuildMinimalDex()
    {
        var dex = new DexFile("035");
        dex.Strings.Add("Lcom/example/Hello;");
        dex.Strings.Add("Ljava/lang/Object;");
        dex.Strings.Add("main");
        dex.Strings.Add("V");
        dex.Strings.Add("([Ljava/lang/String;)V");
        dex.Strings.Add("Hello.java");
        dex.Strings.Add("[Ljava/lang/String;");

        dex.Types.Add("Lcom/example/Hello;");
        dex.Types.Add("Ljava/lang/Object;");
        dex.Types.Add("V");
        dex.Types.Add("[Ljava/lang/String;");

        dex.Protos.Add(new ProtoDefinition
        {
            ShortyIndex = 4,
            ReturnTypeIndex = 2,
            ParameterTypeIndices = [3]
        });

        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });

        var code = new CodeItem
        {
            RegistersSize = 1,
            InsSize = 0,
            OutsSize = 0,
            RawInsns = [0x0012, 0x000E]
        };

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1,
            SourceFileIndex = 5
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public | AccessFlags.Static,
            Code = code
        });
        dex.Classes.Add(cls);

        return dex;
    }
}
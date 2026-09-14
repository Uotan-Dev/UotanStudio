using System.Text;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Exercises policy precedence, mount identity and conservative file context inheritance.</summary>
public sealed class SelinuxFileLabelInferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uotan-selinux-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> projection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreservedInodeEntry> originals = new(StringComparer.Ordinal)
    {
        [""] = Entry("", "system_file", true),
        ["bin"] = Entry("bin", "system_file", true),
        ["bin/sh"] = Entry("bin/sh", "shell_exec"),
        ["bin/init"] = Entry("bin/init", "init_exec")
    };

    [Theory]
    [InlineData("bin/new", false, "new_exec")]
    [InlineData("bin/other", false, "bin_file")]
    [InlineData("bin/new", true, "bin_dir")]
    [InlineData("bin/new.extra", false, "bin_file")]
    public void ExactRulesBeatRegexAndKindsAreRespected(string path, bool directory, string expected)
    {
        Policy("""
            /system/bin/new -- u:object_r:new_exec:s0
            /system(/.*)? u:object_r:system_file:s0
            /system/bin/.* -- u:object_r:bin_file:s0
            /system/bin/.* -d u:object_r:bin_dir:s0
            """);
        Assert.Equal(Context(expected), Infer(path, directory));
    }

    [Theory]
    [InlineData("system_a", "/system/bin/new", "bin/new")]
    [InlineData("vendor_b", "/vendor/bin/new", "bin/new")]
    [InlineData("userdata", "/data/bin/new", "bin/new")]
    public void RuntimePathsUsePartitionIdentity(string partition, string rulePath, string path)
    {
        Policy($"{rulePath} u:object_r:chosen_file:s0");
        Assert.Equal(Context("chosen_file"), Infer(path, partition: partition));
    }

    [Fact]
    public void SystemAsRootDoesNotDoublePrefixSystem()
    {
        originals.Remove("bin");
        originals["system"] = Entry("system", "system_file", true);
        Policy("/system/bin/new u:object_r:chosen_file:s0");
        Assert.Equal(Context("chosen_file"), Infer("system/bin/new"));
    }

    [Fact]
    public void SpecializedEntrypointsAndProcessDomainsAreNeverSiblingDefaults()
    {
        originals["bin/domain"] = Entry("bin/domain", "shell");
        originals["bin/domain"].Xattrs!["security.selinux"] = Encoding.UTF8.GetBytes("u:r:shell:s0\0");
        Assert.Equal(Context("system_file"), Infer("bin/new"));
        Assert.Equal(Context("system_file"), Infer("bin/new-dir", true));
    }

    [Fact]
    public void ConflictingSiblingLabelsFallBackToParentButExtensionConsensusWins()
    {
        originals["bin/a.conf"] = Entry("bin/a.conf", "config_file");
        originals["bin/b.dat"] = Entry("bin/b.dat", "data_file");
        Assert.Equal(Context("config_file"), Infer("bin/new.conf"));
        Assert.Equal(Context("system_file"), Infer("bin/unknown"));
    }

    [Fact]
    public void ConflictingPolicyFilesAreNotOrderedByHostFilename()
    {
        Policy("/system/bin/new u:object_r:first_file:s0");
        Policy("/system/bin/new u:object_r:second_file:s0", "vendor_file_contexts");
        Assert.Throws<InvalidDataException>(() => Infer("bin/new"));
    }

    [Theory]
    [InlineData("/system/.* <<none>>")]
    [InlineData("/system/.* u:r:init:s0")]
    [InlineData(@"/system/(?=bin).* u:object_r:system_file:s0")]
    [InlineData(@"/system/\w+ u:object_r:system_file:s0")]
    [InlineData("/system/[ u:object_r:system_file:s0")]
    public void ExclusionsAndUnsupportedPoliciesCannotSilentlyFallBack(string rule)
    {
        Policy(rule);
        Assert.Throws<InvalidDataException>(() => Infer("bin/new"));
    }

    [Fact]
    public void RegexUsesUtf8BytesAndFullPathMatching()
    {
        Policy("/system/bin/.. u:object_r:two_bytes_file:s0");
        Assert.Equal(Context("two_bytes_file"), Infer("bin/é"));
        Assert.Equal(Context("system_file"), Infer("bin/abc"));
    }

    [Fact]
    public void UnknownMountDoesNotApplySystemRules()
    {
        Policy("/system/bin/new u:object_r:chosen_file:s0");
        Assert.Equal(Context("system_file"), Infer("bin/new", partition: "unknown"));
    }

    [Fact]
    public void LabeledImageWithoutSafeDonorFailsButUnlabeledImageStaysUnlabeled()
    {
        originals.Clear();
        originals[""] = Entry("", "init_exec", true);
        Assert.Throws<InvalidDataException>(() => Infer("new"));
        originals[""].Xattrs = null;
        Assert.Null(Infer("new"));
    }

    [Fact]
    public void OtherExtractionTreesAndGeneratedSidecarsAreNotPolicySources()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "system_file_contexts"), "/system/bin/new u:object_r:wrong_file:s0");
        projection["system_file_contexts"] = "file:test";
        var other = Path.Combine(root, "other", "etc", "selinux");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "plat_file_contexts"), "/system/bin/new u:object_r:wrong_file:s0");
        projection["other/etc/selinux/plat_file_contexts"] = "file:test";
        Assert.Equal(Context("system_file"), Infer("bin/new"));
    }

    [Fact]
    public void NewExecutableDoesNotTakeANonExecutableConfigurationDonor()
    {
        originals["bin/config"] = Entry("bin/config", "config_file");
        var label = new SelinuxFileLabelInference(root, originals, projection, "system").Infer("bin/new", false, true);
        Assert.Equal(Context("system_file"), Encoding.UTF8.GetString(label!).TrimEnd('\0'));
    }

    [Theory]
    [InlineData(16385)]
    [InlineData(8388609)]
    public void OversizedPoliciesAreRejected(int length)
    {
        Policy(new string('x', length));
        Assert.Throws<InvalidDataException>(() => Infer("bin/new"));
    }

    private void Policy(string text, string name = "plat_file_contexts")
    {
        var relative = "etc/selinux/" + name;
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        projection[relative] = "file:test";
    }
    private string? Infer(string path, bool directory = false, string partition = "system")
    {
        var label = new SelinuxFileLabelInference(root, originals, projection, partition).Infer(path, directory);
        return label is null ? null : Encoding.UTF8.GetString(label).TrimEnd('\0');
    }
    private static string Context(string type) => "u:object_r:" + type + ":s0";
    private static PreservedInodeEntry Entry(string path, string type, bool directory = false) => new()
    {
        Path = path,
        Mode = (ushort)(directory ? 0x41ED : 0x81A4),
        Xattrs = new() { ["security.selinux"] = Encoding.UTF8.GetBytes(Context(type) + '\0') }
    };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
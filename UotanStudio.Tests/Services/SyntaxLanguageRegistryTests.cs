using UotanStudio.Pages.DockControl;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Syntax language registry tests: verify extension → TextMate language id mapping and the fallback
/// for unknown extensions.
/// <para>语法语言注册表测试：验证扩展名 → TextMate 语言标识映射及未知扩展名的回退。</para>
/// </summary>
public sealed class SyntaxLanguageRegistryTests
{
    [Theory]
    [InlineData("build.prop", "ini")]
    [InlineData("system.prop", "ini")]
    [InlineData("init.rc", "ini")]
    [InlineData("config.ini", "ini")]
    [InlineData("AndroidManifest.xml", "xml")]
    [InlineData("script.sh", "shell")]
    [InlineData("Android.mk", "makefile")]
    [InlineData("main.c", "c")]
    [InlineData("header.h", "c")]
    [InlineData("main.cpp", "cpp")]
    [InlineData("Program.cs", "csharp")]
    [InlineData("README.md", "markdown")]
    [InlineData("values.yaml", "yaml")]
    [InlineData("values.yml", "yaml")]
    [InlineData("unknown.xyz", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetLanguageId_MapsExtensions(string? fileName, string? expected)
    {
        Assert.Equal(expected, SyntaxLanguageRegistry.GetLanguageId(fileName!));
    }
}
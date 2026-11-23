using UotanStudio.Services.Workspace.Documents;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Services;

public class DocumentOpenRoutingTests
{
    private const string PythonSource = "import string\nimport sys \n\nif __name__ == \"__main__\":\n    pass\n";

    [Fact]
    public void ResolveMode_PythonExtensionWithImportStatement_OpensAsText()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("captital.py", PythonSource);
        var header = File.ReadAllBytes(path);

        var extensionOnly = DocumentFormatPolicy.ResolveMode(path);
        var withHeader = DocumentFormatPolicy.ResolveMode(path, header);

        Assert.Equal(EditorOpenMode.Text, extensionOnly);
        Assert.Equal(EditorOpenMode.Text, withHeader);
    }

    [Fact]
    public void ResolveMode_UnknownExtensionWithPythonTextMagic_OpensAsText()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("captital.probe", PythonSource);
        var header = File.ReadAllBytes(path);

        var mode = DocumentFormatPolicy.ResolveMode(path, header);

        Assert.Equal(EditorOpenMode.Text, mode);
    }
}
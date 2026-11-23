using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FileAssociationServiceTests
{
    [Fact]
    public void BuildWindowsOpenCommand_QuotesExecutableAndPlaceholder()
    {
        var command = FileAssociationService.BuildWindowsOpenCommand(@"C:\Tools\UotanStudio.Desktop.exe");

        Assert.Equal("\"C:\\Tools\\UotanStudio.Desktop.exe\" \"%1\"", command);
    }
}
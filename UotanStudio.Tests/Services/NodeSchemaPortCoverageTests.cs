using UotanStudio.Services;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeSchemaPortCoverageTests
{
    [Fact]
    public void CreateAllBuiltInNodes_NoMissingPortWarnings()
    {
        var warnings = new List<string>();
        var unsubscribe = LogService.Instance.Subscribe((_, entry) =>
        {
            if (entry.Source == "NodeSchema")
            {
                warnings.Add(entry.Message);
            }
        });
        try
        {
            foreach (var descriptor in NodeFactoryRegistrar.Descriptors)
            {
                if (NodeFactoryRegistrar.BuiltInTypeKeys.Contains(descriptor.TypeKey))
                {
                    descriptor.Factory();
                }
            }
        }
        finally
        {
            unsubscribe();
        }

        Assert.True(warnings.Count == 0, string.Join(Environment.NewLine, warnings));
    }
}
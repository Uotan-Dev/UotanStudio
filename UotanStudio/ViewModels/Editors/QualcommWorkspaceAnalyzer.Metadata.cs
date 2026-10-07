using System.Globalization;
using UotanStudio.Firmware.Executable.Qualcomm;

namespace UotanStudio.ViewModels.Editors;

internal sealed partial class QualcommWorkspaceAnalyzer
{
    private static IReadOnlyList<QualcommReportField> MetadataFields(QualcommAnalysisResult result)
    {
        List<QualcommReportField> rows = [];
        foreach (var region in result.Regions)
        {
            rows.Add(Field(region.Name, $"0x{region.Offset:X} + 0x{region.Length:X}"));
        }
        foreach (var metadata in result.SigningMetadata)
        {
            var group = metadata.Authority == "Common" ? "MBN v7" :
                $"{metadata.Authority} / {(metadata.FromCertificate ? "X.509" : "MBN")}";
            rows.Add(Field("MetadataLayout", metadata.Layout, true, group));
            if (metadata.FileOffset is { } offset)
            {
                rows.Add(Field("FileOffset", $"0x{offset:X}", group: group));
            }
            if (metadata.MajorVersion is { } major && metadata.MinorVersion is { } minor)
            {
                rows.Add(Field("MetadataVersion", $"{major}.{minor}", group: group));
            }
            Add("SoftwareId", metadata.SoftwareId);
            Add("SecondarySoftwareId", metadata.SecondarySoftwareId);
            Add("HardwareId", metadata.HardwareId);
            Add("OemId", metadata.OemId);
            Add("ModelId", metadata.ModelId);
            Add("ApplicationId", metadata.ApplicationId);
            Add("SoftwareSize", metadata.SoftwareSize);
            Add("AntiRollbackVersion", metadata.AntiRollbackVersion, decimalValue: true);
            Add("RootSlot", metadata.RootCertificateSlot, decimalValue: true);
            Add("Flags", metadata.Flags);
            Add("SocFeatureId", metadata.SocFeatureId);
            Add("JtagId", metadata.JtagId);
            Add("SocLifecycle", metadata.SocLifecycleState);
            Add("OemLifecycle", metadata.OemLifecycleState);
            Add("RootHashAlgorithm", metadata.RootHashAlgorithm);
            if (metadata.SocVersions.Count > 0)
            {
                rows.Add(Field("SocVersions", string.Join(", ", metadata.SocVersions.Select(value => $"0x{value:X8}")), group: group));
            }
            if (metadata.SerialNumbers.Count > 0)
            {
                rows.Add(Field("SerialNumbers", string.Join(", ", metadata.SerialNumbers.Select(value => $"0x{value:X16}")), group: group));
            }
            if (metadata.RootHash is { } hash)
            {
                rows.Add(Field("DeclaredRootHash", hash, group: group));
            }

            void Add(string name, ulong? value, bool decimalValue = false)
            {
                if (value.HasValue)
                {
                    rows.Add(Field(name, decimalValue ? value.Value.ToString(CultureInfo.InvariantCulture) :
                        $"0x{value.Value:X}", group: group));
                }
            }
        }
        foreach (var certificate in result.Certificates)
        {
            foreach (var attribute in certificate.Attributes)
            {
                rows.Add(new(attribute.Name, attribute.Value, Group: $"{certificate.Authority} #{certificate.Index} / OU"));
            }
        }
        return rows.AsReadOnly();
    }
}
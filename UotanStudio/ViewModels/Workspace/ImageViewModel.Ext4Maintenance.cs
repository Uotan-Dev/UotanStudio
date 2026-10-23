using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Native;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    internal async Task<string> MinimizeExistingExt4Async(ImageEntryViewModel image, bool repair)
    {
        if (IsOperating)
        {
            throw new InvalidOperationException("Another image operation is in progress.");
        }
        var source = GetSelectedSourcePath(image, "EXT4 maintenance")
            ?? throw new FileNotFoundException("Selected EXT4 image is not in the active project.");
        if (ImageInputService.DetectFormat(source) is not ("ext4" or "sparse"))
        {
            throw new InvalidDataException("Selected file is not an EXT4 or Android Sparse image.");
        }
        var project = ProjectContextService.Instance.ActiveSubProject
            ?? throw new InvalidOperationException("No active sub-project.");
        var directory = Directory.CreateDirectory(project.OutputDir).FullName;
        var name = Path.GetFileNameWithoutExtension(source) + (repair ? ".repaired-min" : ".min") + ".img";
        var destination = Path.Combine(directory, GetUniqueFileName(directory, name));
        var distro = ProjectSettingViewModel.EnsureInstance().F2fsNativeWslDistribution;

        IsOperating = true;
        ProgressPercent = -1;
        try
        {
            StatusText = LanguageService.Instance.GetString("Image_Ext4Maintaining");
            await Ext4ImageMaintenance.MinimizeAsync(source, destination,
                new NativeFileSystemTools(distro), repairBeforeResize: repair);
            StatusText = string.Format(LanguageService.Instance.GetString("Image_Ext4Maintained"), destination);
            LogService.Instance.Write(LogLevel.Info, "Images", StatusText);
            if (CreateOutputOperation(HistoryOperationKind.SynthesizeImg,
                    $"Minimize EXT4 {image.DisplayName}", [destination]) is { } history)
            {
                OperationHistoryService.Instance.Record(history.Description, history,
                    () => UndoOutput(history), () => RedoOutput(history));
            }
            return destination;
        }
        catch (Exception ex)
        {
            StatusText = LanguageService.Instance.GetString("Image_Ext4MaintenanceFailed");
            LogService.Instance.Write(LogLevel.Error, "Images", ex.Message);
            throw;
        }
        finally
        {
            IsOperating = false;
        }
    }
}
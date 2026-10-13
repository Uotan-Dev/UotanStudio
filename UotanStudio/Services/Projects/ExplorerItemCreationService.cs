using Avalonia.Controls;
using Ursa.Controls;

namespace UotanStudio.Services.Projects;

/// <summary>Shared explorer and File-menu creation dialogs.<para>资源管理器与文件菜单共用的新建对话框。</para></summary>
internal static class ExplorerItemCreationService
{
    internal static async Task<string?> CreateAsync(Window owner, string directory, bool isDirectory)
    {
        var language = LanguageService.Instance;
        var name = await DialogService.InputAsync(owner,
            language.GetString(isDirectory ? "Dialog_NewFolder" : "Dialog_NewFile"));
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            if (name is "." or ".." || Path.IsPathRooted(name)
                || name.AsSpan().ContainsAny(PathSearchValues.InvalidFileName)
                || name.Contains('/') || name.Contains('\\'))
                throw new IOException(language.GetString("Explorer_InvalidItemName"));
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var path = Path.Combine(directory, name);
            if (isDirectory)
            {
                if (Directory.Exists(path) || File.Exists(path))
                    throw new IOException(string.Format(language.GetString("Explorer_ItemAlreadyExists"), name));
                Directory.CreateDirectory(path);
            }
            else
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            return path;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", $"Create item failed: {ex.Message}");
            await DialogService.ShowAsync(owner,
                language.GetString(isDirectory ? "Dialog_NewFolderFailed" : "Dialog_NewFileFailed"),
                UserError.Describe(ex), MessageBoxIcon.Error);
            return null;
        }
    }
}
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Workspace;

public partial class AiAssistantView : UserControl
{
    public AiAssistantView() => InitializeComponent();

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel model || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var conversation = model.SelectedConversation;
        conversation.AttachmentError = null;
        AddFilesButton.IsEnabled = false;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("AI_AddFiles"),
                AllowMultiple = true,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            foreach (var file in files)
            {
                using (file)
                    conversation.AddAttachment(file.Name, file.TryGetLocalPath() ?? file.Path.ToString());
            }
        }
        catch (Exception)
        {
            conversation.AttachmentError = LanguageService.Instance.GetString("AI_AddFilesFailed");
        }
        finally { AddFilesButton.IsEnabled = true; }
    }
}

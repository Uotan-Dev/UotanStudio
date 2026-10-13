using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>Conversation state for a docked AI assistant.<para>停靠式 AI 助手的会话状态。</para></summary>
public partial class AiAssistantViewModel : Tool
{
    public AiSettingsViewModel Settings { get; }
    public ObservableCollection<AiConversationViewModel> Conversations { get; } = [];
    public IReadOnlyList<AiModeOptionViewModel> ModeOptions { get; } =
        [new("AI_Chat"), new("AI_Agent")];

    [ObservableProperty] private AiConversationViewModel _selectedConversation;

    public IRelayCommand SendMessageCommand { get; } = new RelayCommand(() => { }, () => false);

    public AiAssistantViewModel(AiSettingsViewModel settings)
    {
        Settings = settings;
        Id = "AiAssistant";
        Title = LanguageService.Instance.GetString("Dock_AiAssistant");
        CanClose = true;
        _selectedConversation = new AiConversationViewModel();
        Conversations.Add(_selectedConversation);
        WeakReferenceMessenger.Default.Register<AiAssistantViewModel, LanguageStateChangedMessage>(this,
            static (recipient, _) =>
            {
                recipient.Title = LanguageService.Instance.GetString("Dock_AiAssistant");
                foreach (var option in recipient.ModeOptions)
                    option.RefreshDisplayName();
                foreach (var conversation in recipient.Conversations)
                    conversation.RefreshTitle();
            });
    }

    [RelayCommand]
    private void NewConversation()
    {
        var conversation = new AiConversationViewModel { ModeIndex = SelectedConversation.ModeIndex };
        Conversations.Insert(0, conversation);
        SelectedConversation = conversation;
    }

    [RelayCommand]
    private void ClearConversation()
    {
        SelectedConversation.DraftText = string.Empty;
        SelectedConversation.Messages.Clear();
        SelectedConversation.IncludeActiveDocument = false;
        SelectedConversation.Attachments.Clear();
        SelectedConversation.AttachmentError = null;
    }

    [RelayCommand]
    private void OpenSettings() =>
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Settings));
}

/// <summary>Stable, localized mode choice.<para>实例稳定的本地化模式选项。</para></summary>
/// <param name="resourceKey">Key selecting the localized mode label.<para>选择本地化模式文本的键。</para></param>
public sealed class AiModeOptionViewModel(string resourceKey) : ViewModelBase
{
    public string DisplayName => LanguageService.Instance.GetString(resourceKey);

    internal void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>Local conversation draft and display data.<para>本地会话草稿和显示数据。</para></summary>
public partial class AiConversationViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _draftText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAgent))]
    private int _modeIndex = 1;

    [ObservableProperty] private bool _includeActiveDocument;
    [ObservableProperty] private string? _attachmentError;

    public bool IsAgent => ModeIndex == 1;
    public string Title => string.IsNullOrWhiteSpace(DraftText)
        ? Attachments.FirstOrDefault()?.Name ?? LanguageService.Instance.GetString("AI_NewConversation")
        : DraftText.Trim().Split('\n', 2)[0];
    public ObservableCollection<AiMessage> Messages { get; } = [];
    public ObservableCollection<AiAttachment> Attachments { get; } = [];

    public AiConversationViewModel()
    {
        Attachments.CollectionChanged += (_, _) => RefreshTitle();
    }

    internal void AddAttachment(string name, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Attachments.Any(attachment => string.Equals(attachment.Path, path, comparison)))
            Attachments.Add(new AiAttachment(name, path));
    }

    [RelayCommand]
    private void RemoveAttachment(AiAttachment? attachment)
    {
        if (attachment is not null) Attachments.Remove(attachment);
    }

    internal void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>Message display data without transport behavior.<para>不含传输行为的消息显示数据。</para></summary>
/// <param name="Author">Role or identity that produced the message.<para>产生该消息的角色或身份。</para></param>
/// <param name="Text">Message body text.<para>消息正文文本。</para></param>
public sealed record AiMessage(string Author, string Text);

/// <summary>Selected file name and location only; no file content.<para>仅包含所选文件名和位置，不含文件内容。</para></summary>
/// <param name="Name">Displayed file name.<para>显示用的文件名。</para></param>
/// <param name="Path">Local path of the selected file.<para>所选文件的本地路径。</para></param>
public sealed record AiAttachment(string Name, string Path);
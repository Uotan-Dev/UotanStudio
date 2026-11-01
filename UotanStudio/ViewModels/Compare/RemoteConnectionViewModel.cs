using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Display wrapper for a selectable remote protocol option.
/// <para>可选远程协议选项的显示包装类。</para>
/// </summary>
/// <param name="Protocol">The remote protocol value.<para>远程协议枚举值。</para></param>
/// <param name="DisplayName">The user-facing protocol name.<para>面向用户的协议显示名称。</para></param>
public sealed record RemoteProtocolOption(RemoteProtocol Protocol, string DisplayName);

/// <summary>
/// Display wrapper for a selectable encryption mode option.
/// <para>可选加密模式选项的显示包装类。</para>
/// </summary>
/// <param name="Mode">The encryption mode value.<para>加密模式枚举值。</para></param>
/// <param name="DisplayName">The user-facing encryption mode name.<para>面向用户的加密模式显示名称。</para></param>
public sealed record RemoteEncryptionOption(RemoteEncryptionMode Mode, string DisplayName);

/// <summary>
/// View model representing one remote connection history item in the DataGrid.
/// <para>表示数据表格中一条远程连接历史记录的视图模型。</para>
/// </summary>
public sealed partial class RemoteConnectionProfileItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the underlying remote connection profile.
    /// <para>获取底层的远程连接配置档案。</para>
    /// </summary>
    /// <value>The underlying connection profile.<para>底层的连接配置。</para></value>
    public RemoteConnectionProfile Profile { get; }

    /// <summary>Gets the unique profile identifier.<para>获取配置唯一标识。</para></summary>
    /// <value>The profile identifier string.<para>配置唯一标识字符串。</para></value>
    public string Id => Profile.Id;

    /// <summary>Gets the protocol.<para>获取协议类型。</para></summary>
    /// <value>The protocol enum value.<para>协议枚举值。</para></value>
    public RemoteProtocol Protocol => Profile.Protocol;

    /// <summary>Gets the encryption mode.<para>获取加密模式。</para></summary>
    /// <value>The encryption mode enum value.<para>加密模式枚举值。</para></value>
    public RemoteEncryptionMode Encryption => Profile.Encryption;

    /// <summary>Gets the host name or IP address.<para>获取主机名或 IP 地址。</para></summary>
    /// <value>The host name or IP address.<para>主机名或 IP 地址。</para></value>
    public string Host => Profile.Host;

    /// <summary>Gets the port number.<para>获取端口号。</para></summary>
    /// <value>The port number.<para>端口号。</para></value>
    public int Port => Profile.Port;

    /// <summary>Gets the username.<para>获取用户名。</para></summary>
    /// <value>The username.<para>用户名。</para></value>
    public string Username => Profile.Username;

    /// <summary>Gets whether anonymous login is used.<para>获取是否为匿名登录。</para></summary>
    /// <value><c>true</c> when anonymous; otherwise <c>false</c>.<para>匿名登录为 <c>true</c>；否则为 <c>false</c>。</para></value>
    public bool IsAnonymous => Profile.IsAnonymous;

    /// <summary>Gets the last connected timestamp.<para>获取上次连接时间戳。</para></summary>
    /// <value>The timestamp when last connected, or null.<para>上次连接时间戳，未连接时为 null。</para></value>
    public DateTimeOffset? LastConnected => Profile.LastConnected;

    /// <summary>Gets the localized or standardized protocol display name.<para>获取协议的显示名称。</para></summary>
    /// <value>The protocol name string.<para>协议名称字符串。</para></value>
    public string ProtocolDisplayName => Protocol switch
    {
        RemoteProtocol.Sftp => "SFTP",
        RemoteProtocol.Scp => "SCP",
        RemoteProtocol.Ftp => "FTP",
        RemoteProtocol.WebDav => "WebDAV",
        _ => Protocol.ToString()
    };

    /// <summary>Gets the localized encryption mode display name.<para>获取加密模式的显示名称。</para></summary>
    /// <value>The encryption mode name string.<para>加密模式名称字符串。</para></value>
    public string EncryptionDisplayName => Encryption switch
    {
        RemoteEncryptionMode.None => LanguageService.Instance.GetString("Remote_EncryptNone"),
        RemoteEncryptionMode.TlsImplicit => LanguageService.Instance.GetString("Remote_EncryptTlsImplicit"),
        RemoteEncryptionMode.TlsExplicit => LanguageService.Instance.GetString("Remote_EncryptTlsExplicit"),
        RemoteEncryptionMode.Ssh => LanguageService.Instance.GetString("Remote_EncryptSsh"),
        RemoteEncryptionMode.Https => LanguageService.Instance.GetString("Remote_EncryptHttps"),
        _ => Encryption.ToString()
    };

    /// <summary>Gets the user-friendly username display string.<para>获取用户名的显示文本。</para></summary>
    /// <value>The username or anonymous indicator.<para>用户名或匿名指示文本。</para></value>
    public string DisplayUser => IsAnonymous
        ? LanguageService.Instance.GetString("Remote_AnonymousDisplay")
        : (string.IsNullOrWhiteSpace(Username) ? "-" : Username);

    /// <summary>Gets the formatted last connected date and time.<para>获取格式化后的上次连接时间。</para></summary>
    /// <value>The formatted date-time string or dash when empty.<para>格式化时间字符串，为空时为横线。</para></value>
    public string LastConnectedDisplay => LastConnected?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    /// <summary>
    /// Initializes a new item view model for the specified profile.
    /// <para>用指定的连接档案初始化条目视图模型。</para>
    /// </summary>
    /// <param name="profile">The underlying remote connection profile.<para>底层远程连接配置。</para></param>
    public RemoteConnectionProfileItemViewModel(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
    }
}

/// <summary>
/// View model for the remote file connection dialog window.
/// <para>远程文件连接对话框窗口的视图模型。</para>
/// </summary>
public sealed partial class RemoteConnectionViewModel : ObservableObject
{
    [ObservableProperty] private RemoteProtocolOption _selectedProtocolOption;
    [ObservableProperty] private RemoteEncryptionOption _selectedEncryptionOption;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isAnonymous;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private RemoteConnectionProfileItemViewModel? _selectedHistoryItem;

    /// <summary>
    /// Gets the list of supported remote file protocols.
    /// <para>获取支持的远程文件协议列表。</para>
    /// </summary>
    /// <value>The protocol options list.<para>协议选项列表。</para></value>
    public IReadOnlyList<RemoteProtocolOption> Protocols { get; }

    /// <summary>
    /// Gets the collection of available encryption modes for the currently selected protocol.
    /// <para>获取当前选定协议下可用的加密模式集合。</para>
    /// </summary>
    /// <value>The available encryption options collection.<para>可用的加密选项集合。</para></value>
    public ObservableCollection<RemoteEncryptionOption> AvailableEncryptions { get; } = [];

    /// <summary>
    /// Gets the collection of saved remote connection history profiles.
    /// <para>获取保存的远程连接历史记录集合。</para>
    /// </summary>
    /// <value>The connection history observable collection.<para>连接历史的可观察集合。</para></value>
    public ObservableCollection<RemoteConnectionProfileItemViewModel> History { get; } = [];

    /// <summary>
    /// Event raised when the dialog should close upon a successful connection or cancellation.
    /// <para>当成功连接或取消而需要关闭对话框时触发的事件。</para>
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Gets the profile resulting from a successful connect action, or null if cancelled.
    /// <para>获取成功连接操作产生的配置，若取消则为 null。</para>
    /// </summary>
    /// <value>The resulting connection profile, or null.<para>连接产生的配置档案，或 null。</para></value>
    public RemoteConnectionProfile? ConnectedProfile { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="RemoteConnectionViewModel"/> with default options and loads history.
    /// <para>初始化 <see cref="RemoteConnectionViewModel"/> 的新实例，并加载默认选项与历史记录。</para>
    /// </summary>
    public RemoteConnectionViewModel()
    {
        Protocols =
        [
            new(RemoteProtocol.Sftp, "SFTP"),
            new(RemoteProtocol.Scp, "SCP"),
            new(RemoteProtocol.Ftp, "FTP"),
            new(RemoteProtocol.WebDav, "WebDAV")
        ];

        _selectedProtocolOption = Protocols[0];
        UpdateEncryptions(RemoteProtocol.Sftp);
        _selectedEncryptionOption = AvailableEncryptions[0];

        LoadHistory();
    }

    partial void OnSelectedProtocolOptionChanged(RemoteProtocolOption value)
    {
        UpdateEncryptions(value.Protocol);
        Port = GetDefaultPort(value.Protocol, SelectedEncryptionOption.Mode);
    }

    partial void OnSelectedEncryptionOptionChanged(RemoteEncryptionOption value)
    {
        if (SelectedProtocolOption.Protocol == RemoteProtocol.Ftp && (Port == 21 || Port == 990))
        {
            Port = value.Mode == RemoteEncryptionMode.TlsImplicit ? 990 : 21;
        }
        else if (SelectedProtocolOption.Protocol == RemoteProtocol.WebDav && (Port == 80 || Port == 443))
        {
            Port = value.Mode == RemoteEncryptionMode.Https ? 443 : 80;
        }
    }

    partial void OnSelectedHistoryItemChanged(RemoteConnectionProfileItemViewModel? value)
    {
        if (value is not null)
        {
            LoadFromProfile(value.Profile);
        }
    }

    private void UpdateEncryptions(RemoteProtocol protocol)
    {
        AvailableEncryptions.Clear();
        switch (protocol)
        {
            case RemoteProtocol.Ftp:
                AvailableEncryptions.Add(new(RemoteEncryptionMode.None, LanguageService.Instance.GetString("Remote_EncryptNone")));
                AvailableEncryptions.Add(new(RemoteEncryptionMode.TlsExplicit, LanguageService.Instance.GetString("Remote_EncryptTlsExplicit")));
                AvailableEncryptions.Add(new(RemoteEncryptionMode.TlsImplicit, LanguageService.Instance.GetString("Remote_EncryptTlsImplicit")));
                SelectedEncryptionOption = AvailableEncryptions[0];
                break;
            case RemoteProtocol.WebDav:
                AvailableEncryptions.Add(new(RemoteEncryptionMode.None, LanguageService.Instance.GetString("Remote_EncryptHttp")));
                AvailableEncryptions.Add(new(RemoteEncryptionMode.Https, LanguageService.Instance.GetString("Remote_EncryptHttps")));
                SelectedEncryptionOption = AvailableEncryptions[0];
                break;
            case RemoteProtocol.Sftp:
            case RemoteProtocol.Scp:
            default:
                AvailableEncryptions.Add(new(RemoteEncryptionMode.Ssh, LanguageService.Instance.GetString("Remote_EncryptSsh")));
                SelectedEncryptionOption = AvailableEncryptions[0];
                break;
        }
    }

    private static int GetDefaultPort(RemoteProtocol protocol, RemoteEncryptionMode encryption) => protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Scp => 22,
        RemoteProtocol.Ftp => encryption == RemoteEncryptionMode.TlsImplicit ? 990 : 21,
        RemoteProtocol.WebDav => encryption == RemoteEncryptionMode.Https ? 443 : 80,
        _ => 22
    };

    private void LoadHistory()
    {
        History.Clear();
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        foreach (var profile in settings.RemoteConnectionHistory.OrderByDescending(p => p.LastConnected ?? DateTimeOffset.MinValue))
        {
            History.Add(new RemoteConnectionProfileItemViewModel(profile));
        }
    }

    private void SaveHistory()
    {
        var list = History.Select(item => item.Profile).ToList();
        SettingsPersistenceService.Instance.UpdateRemoteConnectionHistory(list);
    }

    /// <summary>
    /// Populates current form fields from a profile model.
    /// <para>从配置模型填充当前表单字段。</para>
    /// </summary>
    /// <param name="profile">The profile to populate from.<para>要读取的配置档案。</para></param>
    public void LoadFromProfile(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var targetProtocol = Protocols.FirstOrDefault(p => p.Protocol == profile.Protocol) ?? Protocols[0];
        if (SelectedProtocolOption != targetProtocol)
        {
            SelectedProtocolOption = targetProtocol;
        }

        UpdateEncryptions(profile.Protocol);
        var targetEncryption = AvailableEncryptions.FirstOrDefault(e => e.Mode == profile.Encryption) ?? AvailableEncryptions[0];
        SelectedEncryptionOption = targetEncryption;

        Host = profile.Host;
        Port = profile.Port;
        Username = profile.Username;
        Password = profile.Password;
        IsAnonymous = profile.IsAnonymous;
    }

    private RemoteConnectionProfile SaveOrUpdateCurrentProfile(bool updateLastConnected)
    {
        var existing = History.FirstOrDefault(h =>
            h.Protocol == SelectedProtocolOption.Protocol &&
            string.Equals(h.Host, Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
            h.Port == Port &&
            string.Equals(h.Username, Username.Trim(), StringComparison.Ordinal));

        var profile = existing?.Profile ?? new RemoteConnectionProfile();
        profile.Protocol = SelectedProtocolOption.Protocol;
        profile.Encryption = SelectedEncryptionOption.Mode;
        profile.Host = Host.Trim();
        profile.Port = Port;
        profile.Username = Username.Trim();
        profile.Password = Password;
        profile.IsAnonymous = IsAnonymous;
        if (updateLastConnected)
        {
            profile.LastConnected = DateTimeOffset.Now;
        }

        if (existing is not null)
        {
            History.Remove(existing);
        }

        History.Insert(0, new RemoteConnectionProfileItemViewModel(profile));
        SaveHistory();
        return profile;
    }

    /// <summary>
    /// Connects using the current form configuration, saving it to history.
    /// <para>使用当前表单配置连接，并保存到历史记录中。</para>
    /// </summary>
    [RelayCommand]
    public void Connect()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            HasError = true;
            StatusMessage = LanguageService.Instance.GetString("Remote_HostRequired");
            return;
        }

        HasError = false;
        ConnectedProfile = SaveOrUpdateCurrentProfile(updateLastConnected: true);
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Saves the current form configuration to history without connecting.
    /// <para>将当前表单配置保存到历史记录中（不触发连接）。</para>
    /// </summary>
    [RelayCommand]
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            HasError = true;
            StatusMessage = LanguageService.Instance.GetString("Remote_HostRequired");
            return;
        }

        HasError = false;
        SaveOrUpdateCurrentProfile(updateLastConnected: false);
    }

    /// <summary>
    /// Reconnects immediately using a saved history item.
    /// <para>一键重新连接指定的历史记录。</para>
    /// </summary>
    /// <param name="item">The target history item to reconnect.<para>要重连的目标历史记录项。</para></param>
    [RelayCommand]
    public void Reconnect(RemoteConnectionProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        LoadFromProfile(item.Profile);
        Connect();
    }

    /// <summary>
    /// Deletes a saved connection profile from history.
    /// <para>从历史记录中删除一条连接配置。</para>
    /// </summary>
    /// <param name="item">The target history item to delete.<para>要删除的目标历史记录项。</para></param>
    [RelayCommand]
    public void Delete(RemoteConnectionProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var target = History.FirstOrDefault(h => h.Id == item.Id) ?? item;
        History.Remove(target);
        SaveHistory();
    }

    /// <summary>
    /// Fills the current form fields from a history item.
    /// <para>将历史记录项的字段填入当前表单。</para>
    /// </summary>
    /// <param name="item">The history item to fill from.<para>要填入的历史记录项。</para></param>
    [RelayCommand]
    public void Fill(RemoteConnectionProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        LoadFromProfile(item.Profile);
    }
}
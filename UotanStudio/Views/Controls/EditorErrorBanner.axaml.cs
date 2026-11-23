using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Shared single-line top error banner with a dismiss button for editor pages, matching the Compare view.
/// <para>编辑器页面共用的顶部单行错误提示条，附带右侧关闭按钮，与对比视图保持一致。</para>
/// </summary>
public partial class EditorErrorBanner : UserControl
{
    /// <summary>
    /// Defines the <see cref="Message"/> property.
    /// <para>定义 <see cref="Message"/> 属性。</para>
    /// </summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<EditorErrorBanner, string?>(
            nameof(Message),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="DismissCommand"/> property.
    /// <para>定义 <see cref="DismissCommand"/> 属性。</para>
    /// </summary>
    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<EditorErrorBanner, ICommand?>(nameof(DismissCommand));

    /// <summary>
    /// Defines the <see cref="DisplayMessage"/> property.
    /// <para>定义 <see cref="DisplayMessage"/> 属性。</para>
    /// </summary>
    public static readonly DirectProperty<EditorErrorBanner, string> DisplayMessageProperty =
        AvaloniaProperty.RegisterDirect<EditorErrorBanner, string>(
            nameof(DisplayMessage),
            o => o.DisplayMessage);

    /// <summary>
    /// Defines the <see cref="HasDisplayMessage"/> property.
    /// <para>定义 <see cref="HasDisplayMessage"/> 属性。</para>
    /// </summary>
    public static readonly DirectProperty<EditorErrorBanner, bool> HasDisplayMessageProperty =
        AvaloniaProperty.RegisterDirect<EditorErrorBanner, bool>(
            nameof(HasDisplayMessage),
            o => o.HasDisplayMessage);

    private string _displayMessage = string.Empty;
    private bool _hasDisplayMessage;

    /// <summary>
    /// Initializes a new instance of <see cref="EditorErrorBanner"/>.
    /// <para>初始化 <see cref="EditorErrorBanner"/> 实例。</para>
    /// </summary>
    public EditorErrorBanner()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the raw error message to display.
    /// <para>获取或设置要显示的原始错误文本。</para>
    /// </summary>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>
    /// Gets or sets the optional command executed when the dismiss button is clicked; when unset, <see cref="Message"/> is cleared directly.
    /// <para>获取或设置点击关闭按钮时执行的可选命令；未设置时直接清空 <see cref="Message"/>。</para>
    /// </summary>
    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    /// <summary>
    /// Gets the single-line normalized error text shown in the banner.
    /// <para>获取显示在提示条中的单行规范化错误文本。</para>
    /// </summary>
    public string DisplayMessage
    {
        get => _displayMessage;
        private set => SetAndRaise(DisplayMessageProperty, ref _displayMessage, value);
    }

    /// <summary>
    /// Gets whether <see cref="DisplayMessage"/> is non-empty.
    /// <para>获取 <see cref="DisplayMessage"/> 是否非空。</para>
    /// </summary>
    public bool HasDisplayMessage
    {
        get => _hasDisplayMessage;
        private set => SetAndRaise(HasDisplayMessageProperty, ref _hasDisplayMessage, value);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageProperty)
        {
            var normalized = NormalizeInlineError(change.GetNewValue<string?>());
            DisplayMessage = normalized;
            HasDisplayMessage = normalized.Length > 0;
        }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if (DismissCommand is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        SetCurrentValue(MessageProperty, string.Empty);
    }

    private static string NormalizeInlineError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var parts = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts);
    }
}
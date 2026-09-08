using UotanStudio.Services;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>关于对话框，展示应用程序的版本与简介信息。</para>
/// About dialog, showing application version and description.
/// </summary>
public partial class AboutDialog : MessageBoxWindow
{
    /// <summary>
    /// <para>初始化关于对话框的新实例。</para>
    /// Initializes a new instance of the AboutDialog.
    /// </summary>
    public AboutDialog()
    {
        InitializeComponent();
        Resources["STRING_MENU_DIALOG_OK"] = LanguageService.Instance.GetString("About_Close");
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        ThemeFollowSystemRadio.IsCheckedChanged += OnThemeRadioButtonChanged;
        ThemeLightRadio.IsCheckedChanged += OnThemeRadioButtonChanged;
        ThemeDarkRadio.IsCheckedChanged += OnThemeRadioButtonChanged;
    }

    /// <summary>
    /// <para>处理主题 RadioButton 的 IsCheckedChanged 事件，根据 Tag 更新 SelectedThemeIndex。</para>
    /// Handles the IsCheckedChanged event for theme RadioButtons, updating SelectedThemeIndex based on Tag.
    /// </summary>
    private void OnThemeRadioButtonChanged(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is RadioButton rb && rb.IsChecked == true)
        {
            if (rb.Tag is int index)
            {
                vm.SelectedThemeIndex = index;
            }
            else if (rb.Tag is string tagStr && int.TryParse(tagStr, out int parsedIndex))
            {
                vm.SelectedThemeIndex = parsedIndex;
            }
        }
    }
}

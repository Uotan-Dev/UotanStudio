using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Flash;

public sealed partial class FlashViewModel
{
    /// <summary>
    /// Pick-time ceiling for an EDL signature file: enough room for many 256-byte blocks without letting a
    /// mistaken selection pull a large file into memory.
    /// <para>EDL 签名文件的选择期上限：足以容纳许多 256 字节块，同时避免误选把大文件读进内存。</para>
    /// </summary>
    private const int MaximumSignatureFileBytes = 1024 * 1024;

    private string? _edlSignaturePath;
    private byte[]? _edlSignatureBytes;
    private string? _signatureHintShownFor;

    /// <summary>
    /// Restores the remembered signature file path; a path whose file no longer exists is dropped from the
    /// page state instead of failing a later operation at the file read.
    /// <para>恢复记住的签名文件路径；文件已不存在的路径从页面状态中丢弃，而不是让后续操作在读取文件时失败。</para>
    /// </summary>
    private void LoadEdlSignatureSetting()
    {
        var path = SettingsPersistenceService.Instance.LoadSettings().EdlSignaturePath;
        _edlSignaturePath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Repaints the signature controls from the page state after the page is attached.
    /// <para>页面附加后按页面状态重绘签名控件。</para>
    /// </summary>
    internal void RefreshEdlSignatureUi() => UpdateEdlSignatureUiState();

    private void UpdateEdlSignatureUiState()
    {
        if (_surface is null)
        {
            return;
        }

        var pathBox = Field<TextBox>("EdlSignaturePathBox");
        pathBox.Text = _edlSignaturePath ?? string.Empty;
        ToolTip.SetTip(pathBox, _edlSignaturePath ?? string.Empty);
        SetControlEnabled("EdlClearSignatureButton", !string.IsNullOrWhiteSpace(_edlSignaturePath));
    }

    /// <summary>
    /// Picks the signature file offered when the programmer refuses commands until vendor authentication
    /// completes; the file is validated here so a wrong shape never reaches session initialization.
    /// <para>选择 programmer 在厂商认证完成前拒绝命令时提交的签名文件；在此完成校验，使尺寸不符的文件不会进入
    /// 会话初始化。</para>
    /// </summary>
    /// <param name="sender">The clicked button. <para>被点击的按钮。</para></param>
    /// <param name="e">The click arguments. <para>点击参数。</para></param>
    internal async void OnEdlChooseSignatureClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_EdlChooseSignature"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (!TryReadSignatureFile(path, out var bytes))
        {
            return;
        }

        _edlSignaturePath = path;
        _edlSignatureBytes = bytes;
        SettingsPersistenceService.Instance.UpdateEdlSignaturePath(path);
        UpdateEdlSignatureUiState();
        AppendLog(string.Format(CultureInfo.InvariantCulture, _localizer.GetString("Flash_EdlSignatureChosen"),
            Path.GetFileName(path), bytes.Length / FirehoseProtocol.VendorSignatureLength));
    }

    /// <summary>
    /// Clears the selected signature file from the page state and from the persisted settings.
    /// <para>从页面状态与持久化设置中清除已选签名文件。</para>
    /// </summary>
    /// <param name="sender">The clicked button. <para>被点击的按钮。</para></param>
    /// <param name="e">The click arguments. <para>点击参数。</para></param>
    internal void OnEdlClearSignatureClick(object? sender, RoutedEventArgs e)
    {
        _edlSignaturePath = null;
        _edlSignatureBytes = null;
        SettingsPersistenceService.Instance.UpdateEdlSignaturePath(string.Empty);
        UpdateEdlSignatureUiState();
    }

    /// <summary>
    /// Resolves the configured signature blocks, reading the file once when the pick was restored from settings.
    /// <para>取出已配置的签名块；选择是从设置恢复的时读取文件一次。</para>
    /// </summary>
    /// <param name="signature">The signature blocks, or <see langword="null"/> when none is configured.
    /// <para>签名块；未配置时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="false"/> when a configured file cannot be used, after logging the reason.
    /// <para>已配置的文件不可用时记录原因并返回 <see langword="false"/>。</para></returns>
    private bool TryLoadEdlSignature(out ReadOnlyMemory<byte>? signature)
    {
        signature = null;
        if (string.IsNullOrWhiteSpace(_edlSignaturePath))
        {
            return true;
        }

        if (_edlSignatureBytes is { Length: > 0 } cached)
        {
            signature = cached;
            return true;
        }

        if (!TryReadSignatureFile(_edlSignaturePath, out var bytes))
        {
            return false;
        }

        _edlSignatureBytes = bytes;
        signature = bytes;
        return true;
    }

    private bool TryReadSignatureFile(string path, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumSignatureFileBytes)
            {
                return ReportSignatureFileInvalid(path);
            }

            var data = File.ReadAllBytes(path);
            if (data.Length % FirehoseProtocol.VendorSignatureLength != 0)
            {
                return ReportSignatureFileInvalid(path);
            }

            bytes = data;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReportSignatureFileInvalid(path);
        }
    }

    private bool ReportSignatureFileInvalid(string path)
    {
        AppendLog(string.Format(CultureInfo.InvariantCulture, _localizer.GetString("Flash_EdlSignatureInvalid"),
            Path.GetFileName(path)));
        return false;
    }

    /// <summary>
    /// Warns once per selected programmer when its bytes carry the authentication refusal marker and no
    /// signature is configured; the programmer still runs because the marker is a hint, not a refusal.
    /// <para>所选 programmer 的字节携带认证拒绝标记且未配置签名时提示一次；programmer 仍照常使用，因为该标记只是
    /// 提示而非拒绝。</para>
    /// </summary>
    /// <param name="hasSignature">Whether a signature file is configured. <para>是否已配置签名文件。</para></param>
    private void WarnIfProgrammerRequiresSignature(bool hasSignature)
    {
        if (hasSignature || EffectiveEdlProgrammer is not { } programmer
            || string.Equals(_signatureHintShownFor, programmer, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!EdlProgrammerAuthMarker.DetectFile(programmer))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        _signatureHintShownFor = programmer;
        AppendLog(_localizer.GetString("Flash_Log_SignatureRequired"));
    }
}
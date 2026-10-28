using Irihi.Iconica.TDesign;
using UotanStudio.ViewModels;
using Td = Irihi.Iconica.TDesign.Icons;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Maps a runtime <see cref="AppIconKind"/> to the native Irihi Iconica TDesign control that renders it.
/// <para>把运行时 <see cref="AppIconKind"/> 映射到实际渲染它的 Irihi Iconica TDesign 原生控件。</para>
/// </summary>
internal static class AppIconFactory
{
    /// <summary>
    /// Creates the Iconica control for the requested semantic icon. The fallback is the generic file glyph so an
    /// unmapped identifier still renders something instead of an empty slot.
    /// <para>为请求的语义图标创建 Iconica 控件。未映射的标识回退到通用文件图标，避免出现空白占位。</para>
    /// </summary>
    /// <param name="kind">The semantic icon identifier. <para>语义图标标识。</para></param>
    /// <returns>The Iconica control that draws the icon. <para>绘制该图标的 Iconica 控件。</para></returns>
    internal static TDesignIconBase Create(AppIconKind kind) => kind switch
    {
        AppIconKind.File => new Td.File(),
        AppIconKind.Folder => new Td.Folder(),
        AppIconKind.ProjectFolder => new Td.FolderDetails(),
        AppIconKind.FileTransfer => new Td.ArrowUpDown1(),
        AppIconKind.HardDisk => new Td.HardDiskStorage(),
        AppIconKind.Chip => new Td.Cpu(),
        AppIconKind.Android => new Td.LogoAndroid(),
        AppIconKind.DeviceTree => new Td.Sitemap(),
        AppIconKind.FileCode => new Td.FileCode(),
        AppIconKind.Database => new Td.DataBase(),
        AppIconKind.FileXml => new Td.FileCode(),
        AppIconKind.Xml => new Td.Code(),
        AppIconKind.Json => new Td.FileJson(),
        AppIconKind.FileCog => new Td.FileSetting(),
        AppIconKind.Script => new Td.Terminal(),
        AppIconKind.Zip => new Td.FileZip(),
        AppIconKind.Archive => new Td.FileZip(),
        AppIconKind.Package => new Td.Gift(),
        AppIconKind.Certificate => new Td.Certificate(),
        AppIconKind.Key => new Td.Key(),
        AppIconKind.Image => new Td.FileImage(),
        AppIconKind.Movie => new Td.Video(),
        AppIconKind.Music => new Td.FileMusic(),
        AppIconKind.Markdown => new Td.FileMarkdown(),
        AppIconKind.Csv => new Td.FileCsv(),
        AppIconKind.Pdf => new Td.FilePdf(),
        AppIconKind.Word => new Td.FileWord(),
        AppIconKind.Excel => new Td.FileExcel(),
        AppIconKind.PowerPoint => new Td.FilePowerpoint(),
        AppIconKind.TextLines => new Td.File(),
        AppIconKind.FolderSearch => new Td.FolderSearch(),
        AppIconKind.RadioTower => new Td.Tower(),
        AppIconKind.CodeBraces => new Td.Brackets(),
        AppIconKind.Float => new Td.ArrowRightUp(),
        AppIconKind.FloatAll => new Td.Window1(),
        AppIconKind.Show => new Td.Browse(),
        AppIconKind.Pin => new Td.Pin(),
        AppIconKind.PinOff => new Td.LockOff(),
        AppIconKind.Tab => new Td.PageTab(),
        AppIconKind.CloseOthers => new Td.CloseCircle(),
        AppIconKind.CloseAll => new Td.CloseRectangle(),
        AppIconKind.Close => new Td.Close(),
        AppIconKind.TabsLeft => new Td.ArrowLeft(),
        AppIconKind.TabsTop => new Td.ArrowUp(),
        AppIconKind.TabsRight => new Td.ArrowRight(),
        AppIconKind.DragHandle => new Td.DragMove(),
        AppIconKind.SplitHorizontal => new Td.ComponentDividerHorizontal(),
        AppIconKind.SplitVertical => new Td.ComponentDividerVertical(),
        AppIconKind.OpenAs => new Td.ArrowRightUp(),
        AppIconKind.Convert => new Td.Swap(),
        AppIconKind.Checksum => new Td.FileSafety(),
        AppIconKind.Extract => new Td.FileDownload(),
        AppIconKind.OpenFolder => new Td.FolderOpen(),
        _ => new Td.File(),
    };
}
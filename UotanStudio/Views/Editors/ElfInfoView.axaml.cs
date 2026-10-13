using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>ELF file-header, program-header and section-header information panel.<para>展示 ELF 文件头、程序头与节区头信息。</para></summary>
public partial class ElfInfoView : UserControl
{
    /// <summary>Initializes the read-only information panel.<para>初始化只读信息视图。</para></summary>
    public ElfInfoView()
    {
        InitializeComponent();
    }
}
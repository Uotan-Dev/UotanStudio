using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels;

/// <summary>Save state of a document that supports save and close confirmation.<para>支持保存与关闭确认的文档的保存状态。</para></summary>
internal interface ISaveableDocument
{
    /// <summary>Whether the document contains unsaved changes.<para>文档是否包含未保存的更改。</para></summary>
    /// <value>The unsaved-changes flag.<para>未保存更改标记。</para></value>
    bool IsDirty { get; }

    /// <summary>The last save or compilation error.<para>最近一次保存或编译错误。</para></summary>
    /// <value>The recorded error text.<para>记录的错误文本。</para></value>
    string CompileError { get; }
}

/// <summary>A document whose existing save operation is exposed as an asynchronous command.<para>将现有保存操作以异步命令暴露的文档。</para></summary>
internal interface ICommandSaveableDocument : ISaveableDocument
{
    /// <summary>The existing save command, including its execution and CanExecute state.<para>现有的保存命令，包含其执行方式与 CanExecute 状态。</para></summary>
    /// <value>The asynchronous save command.<para>异步保存命令。</para></value>
    IAsyncRelayCommand SaveCommand { get; }
}
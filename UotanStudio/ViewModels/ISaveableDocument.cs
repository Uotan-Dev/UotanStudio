using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels;

/// <summary>Save state exposed by the editors already supported by document save and close confirmation.</summary>
internal interface ISaveableDocument
{
    /// <summary>Whether the document contains unsaved changes.</summary>
    bool IsDirty { get; }

    /// <summary>The last save or compilation error.</summary>
    string CompileError { get; }
}

/// <summary>Editors whose existing save operation is exposed as an asynchronous command.</summary>
internal interface ICommandSaveableDocument : ISaveableDocument
{
    /// <summary>The existing save command, including its execution and CanExecute state.</summary>
    IAsyncRelayCommand SaveCommand { get; }
}

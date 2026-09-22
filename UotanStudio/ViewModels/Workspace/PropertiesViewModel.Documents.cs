using System.Collections.Specialized;
using System.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.ViewModels.Workspace;

internal partial class PropertiesViewModel
{
    private Document? _observedDocument;
    private PartitionItemViewModel? _observedPartition;
    private bool _refreshingDocument;

    private void ObserveDocument(Document? document)
    {
        StopObservingDocument();
        if (document is not (ElfInfoViewModel or PartitionEditorViewModel or LogEditorViewModel or DexEditorViewModel or FirmwareInfoViewModel))
        {
            return;
        }
        _observedDocument = document;
        document.PropertyChanged += OnObservedDocumentChanged;
        if (document is PartitionEditorViewModel { SelectedPartition: { } partition })
        {
            _observedPartition = partition;
            partition.PropertyChanged += OnObservedPartitionChanged;
        }
        if (document is LogEditorViewModel log)
        {
            log.EntriesChanged += OnObservedLogEntriesChanged;
        }
    }

    private void StopObservingDocument()
    {
        if (_observedDocument is { } document)
        {
            document.PropertyChanged -= OnObservedDocumentChanged;
            if (document is LogEditorViewModel log)
            {
                log.EntriesChanged -= OnObservedLogEntriesChanged;
            }
        }
        if (_observedPartition is { } partition)
        {
            partition.PropertyChanged -= OnObservedPartitionChanged;
        }
        _observedDocument = null;
        _observedPartition = null;
    }

    private void OnObservedDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        var refresh = sender switch
        {
            ElfInfoViewModel => e.PropertyName is nameof(ElfInfoViewModel.SelectedSegment) or nameof(ElfInfoViewModel.SelectedSection)
                or nameof(ElfInfoViewModel.SelectedPageIndex) or nameof(ElfInfoViewModel.Fields) or nameof(ElfInfoViewModel.IsBusy),
            PartitionEditorViewModel => e.PropertyName is nameof(PartitionEditorViewModel.SelectedPartition)
                or nameof(PartitionEditorViewModel.IsBusy) or nameof(PartitionEditorViewModel.SourceDescription),
            LogEditorViewModel => e.PropertyName is nameof(LogEditorViewModel.IsLoading) or nameof(LogEditorViewModel.ErrorMessage),
            DexEditorViewModel => e.PropertyName is nameof(DexEditorViewModel.SelectedEntry) or nameof(DexEditorViewModel.SelectedResource)
                or nameof(DexEditorViewModel.IsBusy),
            FirmwareInfoViewModel => e.PropertyName is nameof(FirmwareInfoViewModel.IsBusy) or nameof(FirmwareInfoViewModel.Summary),
            _ => false
        };
        if (refresh)
        {
            RefreshObservedDocument();
        }
    }

    private void OnObservedPartitionChanged(object? sender, PropertyChangedEventArgs e) => RefreshObservedDocument();
    private void OnObservedLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshObservedDocument();

    private void RefreshObservedDocument()
    {
        if (_refreshingDocument || _observedDocument is not { } document)
        {
            return;
        }
        _refreshingDocument = true;
        try
        {
            RefreshActiveDocument(DocumentNotifications.Create(document));
            ObserveDocument(document);
        }
        finally
        {
            _refreshingDocument = false;
        }
    }

    private void AddDocumentIdentity(string? path, string title)
    {
        SelectedNodeTitle = path ?? title;
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_FileName", string.IsNullOrEmpty(path) ? title : Path.GetFileName(path)));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_FilePath", path ?? string.Empty));
    }

    private void RefreshForElfDocument(ElfInfoViewModel document)
    {
        ClearProperties();
        ShowProperties(PropertiesContentType.ElfDocument);
        AddDocumentIdentity(document.FilePath, document.Title);
        foreach (var field in document.Fields)
        {
            Properties.Add(CreateDynamicRow(field.Name, field.Value));
        }
        if (document.SelectedPageIndex == 1 && document.SelectedSegment is { } segment)
        {
            AddElf("Segments", segment.Index.ToString());
            AddElf("Type", segment.Type);
            AddElf("Flags", segment.Flags);
            AddElf("Offset", segment.Offset);
            AddElf("VirtualAddress", segment.VirtualAddress);
            AddElf("PhysicalAddress", segment.PhysicalAddress);
            AddElf("FileBytes", segment.FileSize);
            AddElf("MemoryBytes", segment.MemorySize);
            AddElf("Alignment", segment.Alignment);
        }
        if (document.SelectedPageIndex == 2 && document.SelectedSection is { } section)
        {
            AddElf("Sections", section.Index.ToString());
            AddElf("Name", section.Name);
            AddElf("Type", section.Type);
            AddElf("Flags", section.Flags);
            AddElf("Address", section.Address);
            AddElf("Offset", section.Offset);
            AddElf("Size", section.Size);
            AddElf("Link", section.Link.ToString());
            AddElf("Info", section.Info.ToString());
            AddElf("Alignment", section.Alignment);
            AddElf("EntrySize", section.EntrySize);
        }
    }

    private void AddElf(string key, string value) => Properties.Add(CreateLocalizedRow("Dock_Elf_" + key, value));

    private void RefreshForPartitionDocument(PartitionEditorViewModel document)
    {
        ClearProperties();
        ShowProperties(PropertiesContentType.PartitionDocument);
        AddDocumentIdentity(document.FilePath, document.Title);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Partition_Source", document.SourceDescription));
        Properties.Add(CreateLocalizedRow("Dock_Partition_Partitions", document.PartitionCount.ToString()));
        if (document.SelectedPartition is { } partition)
        {
            Properties.Add(CreateLocalizedRow("Dock_Partition_Name", partition.Name));
            Properties.Add(CreateLocalizedRow("Dock_Partition_Start", partition.Start));
            Properties.Add(CreateLocalizedRow("Dock_Partition_Size", partition.Size));
            Properties.Add(CreateLocalizedRow("Dock_Partition_Type", partition.Type));
            if (!string.IsNullOrEmpty(partition.Group))
            {
                Properties.Add(CreateLocalizedRow("Dock_Properties_Partition_Group", partition.Group));
            }
        }
    }

    private void RefreshForLogDocument(LogEditorViewModel document)
    {
        ClearProperties();
        ShowProperties(PropertiesContentType.LogDocument);
        AddDocumentIdentity(document.FilePath, document.Title);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Log_MatchingEntries", document.Entries.Count.ToString()));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Log_Search", document.SearchText));
        if (!string.IsNullOrEmpty(document.ErrorMessage))
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_Log_Error", document.ErrorMessage));
        }
    }
}
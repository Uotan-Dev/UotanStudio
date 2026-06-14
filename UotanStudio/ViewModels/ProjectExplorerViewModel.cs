using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using NodifyM.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UotanStudio.ViewModels
{
    internal class ProjectExplorerViewModel : Tool
    {
        public AvaloniaList<Node> TreeViewContent { get; } = [];

        public ProjectExplorerViewModel()
        {
            TreeViewContent.AddRange(
            Enumerable.Range(1, 10).Select(x => new Node($"Outer {x}",
                Enumerable.Range(1, 5).Select(y => new Node($"Inner {y}",
                    Enumerable.Range(1, 2).Select(z => new Node($"Innermost {z}",
                        Enumerable.Range(1, 2).Select(n => new Node($"1234567 {n}")))))))));
        }
    }
}

public partial class Node(string value, IEnumerable<Node>? subNodes = null) : ObservableObject
{
    public AvaloniaList<Node> SubNodes { get; } = new(subNodes ?? Array.Empty<Node>());
    [ObservableProperty] private string _value = value;
}
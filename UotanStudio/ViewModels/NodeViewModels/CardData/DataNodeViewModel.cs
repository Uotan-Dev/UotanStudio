using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>数据处理节点视图模型，提供文件读取、格式转换、校验和与比较功能。</para>
    /// Data processing node view model, providing file reading, format conversion, checksum, and comparison capabilities.
    /// </summary>
    public class DataNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的数据处理节点。</para>
        /// Creates a data processing node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public DataNodeViewModel(string title) : base(title, NodeCategory.Data)
        {
        }

        /// <summary>
        /// <para>创建默认的文件读取节点。</para>
        /// Creates a default file read node.
        /// </summary>
        public static DataNodeViewModel CreateFileReadNode()
        {
            var node = new DataNodeViewModel("Read File")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Data" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的格式转换节点。</para>
        /// Creates a default format conversion node.
        /// </summary>
        public static DataNodeViewModel CreateConvertNode()
        {
            var node = new DataNodeViewModel("Format Convert")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Input" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Format" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Output" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的校验和节点。</para>
        /// Creates a default checksum node.
        /// </summary>
        public static DataNodeViewModel CreateChecksumNode()
        {
            var node = new DataNodeViewModel("Checksum")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Hash" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的比较节点。</para>
        /// Creates a default comparison node.
        /// </summary>
        public static DataNodeViewModel CreateCompareNode()
        {
            var node = new DataNodeViewModel("Compare")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data A" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Data B" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Match" });
            return node;
        }
    }
}
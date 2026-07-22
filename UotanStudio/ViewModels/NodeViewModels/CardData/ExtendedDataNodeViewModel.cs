using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>扩展数据处理节点视图模型，提供拼接、切片、加密、压缩与字节序转换功能。</para>
    /// Extended data processing node view model, providing concat, slice, encrypt, compress, and byte-order conversion capabilities.
    /// </summary>
    public class ExtendedDataNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的扩展数据处理节点。</para>
        /// Creates an extended data processing node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public ExtendedDataNodeViewModel(string title) : base(title, NodeCategory.Data)
        {
        }

        /// <summary>
        /// <para>创建默认的数据拼接节点。</para>
        /// Creates a default concat node that joins two data streams.
        /// </summary>
        public static ExtendedDataNodeViewModel CreateConcatNode()
        {
            var node = new ExtendedDataNodeViewModel("Concat")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data A" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Data B" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Combined" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的数据切片节点，提取字节区间。</para>
        /// Creates a default slice node that extracts a byte range.
        /// </summary>
        public static ExtendedDataNodeViewModel CreateSliceNode()
        {
            var node = new ExtendedDataNodeViewModel("Slice")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Offset" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Length" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Slice" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的加密节点，使用指定算法加密数据。</para>
        /// Creates a default encrypt node that encrypts data with a specified algorithm.
        /// </summary>
        public static ExtendedDataNodeViewModel CreateEncryptNode()
        {
            var node = new ExtendedDataNodeViewModel("Encrypt")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Key" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Cipher" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的压缩节点，使用指定算法压缩数据。</para>
        /// Creates a default compress node that compresses data with a specified algorithm.
        /// </summary>
        public static ExtendedDataNodeViewModel CreateCompressNode()
        {
            var node = new ExtendedDataNodeViewModel("Compress")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Compressed" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的字节序转换节点。</para>
        /// Creates a default byte-swap node that converts between byte orders.
        /// </summary>
        public static ExtendedDataNodeViewModel CreateByteSwapNode()
        {
            var node = new ExtendedDataNodeViewModel("Byte Swap")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Swapped" });
            return node;
        }
    }
}

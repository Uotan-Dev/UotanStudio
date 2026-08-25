using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

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
                Location = new Point(100, 100),
                TypeKey = "Data.Concat"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data A", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Data B", PortType = NodePortType.Data });
            node.Output.Add(new NodeConnectorViewModel { Title = "Combined", PortType = NodePortType.Data });
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
                Location = new Point(100, 100),
                TypeKey = "Data.Slice"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Offset", PortType = NodePortType.Number });
            node.Input.Add(new NodeConnectorViewModel { Title = "Length", PortType = NodePortType.Number });
            node.Output.Add(new NodeConnectorViewModel { Title = "Slice", PortType = NodePortType.Data });
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
                Location = new Point(100, 100),
                TypeKey = "Data.Encrypt"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Key", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm", PortType = NodePortType.Text });
            node.Output.Add(new NodeConnectorViewModel { Title = "Cipher", PortType = NodePortType.Data });
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
                Location = new Point(100, 100),
                TypeKey = "Data.Compress"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
            node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm", PortType = NodePortType.Text });
            node.Output.Add(new NodeConnectorViewModel { Title = "Compressed", PortType = NodePortType.Data });
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
                Location = new Point(100, 100),
                TypeKey = "Data.ByteSwap"
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
            node.Output.Add(new NodeConnectorViewModel { Title = "Swapped", PortType = NodePortType.Data });
            return node;
        }

        /// <summary>
        /// <para>执行与节点标题对应的扩展数据处理逻辑：Concat 拼接、Slice 切片、Encrypt 加密、Compress 压缩、Byte Swap 字节序转换。</para>
        /// Executes the extended data-processing logic matching the node title: Concat concatenates, Slice extracts a range,
        /// Encrypt encrypts, Compress compresses, and Byte Swap converts byte order.
        /// </summary>
        /// <param name="context">
        /// <para>执行上下文。</para>
        /// The execution context.
        /// </param>
        /// <returns>
        /// <para>表示异步执行的任务。</para>
        /// A task representing the asynchronous execution.
        /// </returns>
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            switch (Title)
            {
                case "Concat":
                    ExecuteConcat(context);
                    break;
                case "Slice":
                    ExecuteSlice(context);
                    break;
                case "Encrypt":
                    ExecuteEncrypt(context);
                    break;
                case "Compress":
                    ExecuteCompress(context);
                    break;
                case "Byte Swap":
                    ExecuteByteSwap(context);
                    break;
                default:
                    return base.ExecuteAsync(context);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private void ExecuteConcat(INodeExecutionContext context)
        {
            var a = NodeDataUtil.ToBytes(ReadInput("Data A")) ?? [];
            var b = NodeDataUtil.ToBytes(ReadInput("Data B")) ?? [];
            var combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0, a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
            WriteOutput("Combined", combined);
            context.Log.Write(LogLevel.Info, Title, $"Concatenated {a.Length}+{b.Length} bytes.  已拼接。");
        }

        private void ExecuteSlice(INodeExecutionContext context)
        {
            var data = NodeDataUtil.ToBytes(ReadInput("Data"));
            if (data == null)
            {
                context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
                return;
            }

            var offset = NodeDataUtil.ToInt(ReadInput("Offset"), 0);
            var lengthInput = ReadInput("Length");
            var length = string.IsNullOrEmpty(Convert.ToString(lengthInput, System.Globalization.CultureInfo.InvariantCulture))
                ? data.Length - offset
                : NodeDataUtil.ToInt(lengthInput, data.Length - offset);

            var start = Math.Clamp(offset, 0, data.Length);
            var end = Math.Clamp(offset + Math.Max(0, length), start, data.Length);
            var slice = data[start..end];
            WriteOutput("Slice", slice);
            context.Log.Write(LogLevel.Info, Title, $"Sliced [{start}..{end}) of {data.Length} bytes.  已切片。");
        }

        private void ExecuteEncrypt(INodeExecutionContext context)
        {
            var data = NodeDataUtil.ToBytes(ReadInput("Data"));
            if (data == null || data.Length == 0)
            {
                context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
                return;
            }

            var key = NodeDataUtil.ToBytes(ReadInput("Key")) ?? Encoding.UTF8.GetBytes("uotan-default-key");
            var algorithm = (Convert.ToString(ReadInput("Algorithm"), System.Globalization.CultureInfo.InvariantCulture) ?? "aes").Trim().ToLowerInvariant();

            byte[] cipher = algorithm switch
            {
                "aes" or "aes-256" => AesEncrypt(data, key),
                _ => XorCipher(data, key)
            };
            WriteOutput("Cipher", cipher);
            context.Log.Write(LogLevel.Info, Title, $"Encrypted {data.Length} bytes with {algorithm}.  已加密。");
        }

        private void ExecuteCompress(INodeExecutionContext context)
        {
            var data = NodeDataUtil.ToBytes(ReadInput("Data"));
            if (data == null)
            {
                context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
                return;
            }

            var algorithm = (Convert.ToString(ReadInput("Algorithm"), System.Globalization.CultureInfo.InvariantCulture) ?? "gzip").Trim().ToLowerInvariant();
            byte[] compressed = algorithm switch
            {
                "deflate" => CompressWith(data, mode => new DeflateStream(mode, CompressionLevel.Optimal)),
                _ => CompressWith(data, mode => new GZipStream(mode, CompressionLevel.Optimal))
            };
            WriteOutput("Compressed", compressed);
            context.Log.Write(LogLevel.Info, Title, $"Compressed {data.Length} -> {compressed.Length} bytes ({algorithm}).  已压缩。");
        }

        private void ExecuteByteSwap(INodeExecutionContext context)
        {
            var data = NodeDataUtil.ToBytes(ReadInput("Data"));
            if (data == null)
            {
                context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
                return;
            }

            var swapped = new byte[data.Length];
            for (var i = 0; i < data.Length; i += 4)
            {
                var n = Math.Min(4, data.Length - i);
                for (var j = 0; j < n; j++)
                {
                    swapped[i + j] = data[i + n - 1 - j];
                }
            }

            WriteOutput("Swapped", swapped);
            context.Log.Write(LogLevel.Info, Title, $"Byte-swapped {data.Length} bytes.  已进行字节序转换。");
        }

        private static byte[] AesEncrypt(byte[] data, byte[] keyMaterial)
        {
            using var aes = Aes.Create();
            aes.Key = SHA256.HashData(keyMaterial);
            aes.IV = RandomNumberGenerator.GetBytes(aes.BlockSize / 8);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
            return [.. aes.IV, .. encrypted];
        }

        private static byte[] XorCipher(byte[] data, byte[] keyMaterial)
        {
            var key = SHA256.HashData(keyMaterial);
            var result = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            }

            return result;
        }

        private static byte[] CompressWith(byte[] data, Func<Stream, Stream> factory)
        {
            using var output = new MemoryStream();
            using (var stream = factory(output))
            {
                stream.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }
    }
}
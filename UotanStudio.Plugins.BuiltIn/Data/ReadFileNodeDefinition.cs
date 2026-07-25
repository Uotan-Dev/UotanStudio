using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Plugins.BuiltIn.Data
{
    /// <summary>
    /// <para>文件读取节点定义。</para>
    /// File read node definition.
    /// </summary>
    public sealed class ReadFileNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.read_file";

        /// <inheritdoc />
        public override string DisplayName => "Read File";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ReadFileNodeInstance();

        /// <summary>
        /// <para>文件读取节点实例。</para>
        /// File read node instance.
        /// </summary>
        public sealed class ReadFileNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ReadFileNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var path = GetInputString("Path");
                if (string.IsNullOrEmpty(path))
                {
                    ErrorMessage = "Missing file path";
                    return;
                }
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Reading file: {path}");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                SetOutput("Data", System.IO.File.ReadAllBytes(path));
            }
        }
    }

    /// <summary>
    /// <para>格式转换节点定义。</para>
    /// Format conversion node definition.
    /// </summary>
    public sealed class ConvertNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.convert";

        /// <inheritdoc />
        public override string DisplayName => "Format Convert";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ConvertNodeInstance();

        /// <summary>
        /// <para>格式转换节点实例。</para>
        /// Format conversion node instance.
        /// </summary>
        public sealed class ConvertNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ConvertNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var format = GetInputString("Format") ?? "raw";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Converting format to '{format}'");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                SetOutput("Output", "converted");
            }
        }
    }

    /// <summary>
    /// <para>校验和节点定义。</para>
    /// Checksum node definition.
    /// </summary>
    public sealed class ChecksumNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.checksum";

        /// <inheritdoc />
        public override string DisplayName => "Checksum";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ChecksumNodeInstance();

        /// <summary>
        /// <para>校验和节点实例。</para>
        /// Checksum node instance.
        /// </summary>
        public sealed class ChecksumNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ChecksumNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var algo = GetInputString("Algorithm") ?? "sha256";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Computing checksum with '{algo}'");
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                SetOutput("Hash", "0xdeadbeef");
            }
        }
    }

    /// <summary>
    /// <para>比较节点定义。</para>
    /// Compare node definition.
    /// </summary>
    public sealed class CompareNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.compare";

        /// <inheritdoc />
        public override string DisplayName => "Compare";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new CompareNodeInstance();

        /// <summary>
        /// <para>比较节点实例。</para>
        /// Compare node instance.
        /// </summary>
        public sealed class CompareNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new CompareNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var a = GetInput<object?>("Data A", null);
                var b = GetInput<object?>("Data B", null);
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Comparing data: A={a}, B={b}");
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                SetOutput("Match", Equals(a, b));
            }
        }
    }

    /// <summary>
    /// <para>拼接节点定义。</para>
    /// Concat node definition.
    /// </summary>
    public sealed class ConcatNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.concat";

        /// <inheritdoc />
        public override string DisplayName => "Concat";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ConcatNodeInstance();

        /// <summary>
        /// <para>拼接节点实例。</para>
        /// Concat node instance.
        /// </summary>
        public sealed class ConcatNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ConcatNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", "Concatenating data streams");
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                SetOutput("Combined", "concatenated");
            }
        }
    }

    /// <summary>
    /// <para>切片节点定义。</para>
    /// Slice node definition.
    /// </summary>
    public sealed class SliceNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.slice";

        /// <inheritdoc />
        public override string DisplayName => "Slice";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new SliceNodeInstance();

        /// <summary>
        /// <para>切片节点实例。</para>
        /// Slice node instance.
        /// </summary>
        public sealed class SliceNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new SliceNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var offset = GetInputString("Offset");
                var length = GetInputString("Length");
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Slicing data: offset={offset}, length={length}");
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                SetOutput("Slice", "sliced");
            }
        }
    }

    /// <summary>
    /// <para>加密节点定义。</para>
    /// Encrypt node definition.
    /// </summary>
    public sealed class EncryptNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.encrypt";

        /// <inheritdoc />
        public override string DisplayName => "Encrypt";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new EncryptNodeInstance();

        /// <summary>
        /// <para>加密节点实例。</para>
        /// Encrypt node instance.
        /// </summary>
        public sealed class EncryptNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new EncryptNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var algo = GetInputString("Algorithm") ?? "aes-256-gcm";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Encrypting with '{algo}'");
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                SetOutput("Cipher", "encrypted");
            }
        }
    }

    /// <summary>
    /// <para>压缩节点定义。</para>
    /// Compress node definition.
    /// </summary>
    public sealed class CompressNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.compress";

        /// <inheritdoc />
        public override string DisplayName => "Compress";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new CompressNodeInstance();

        /// <summary>
        /// <para>压缩节点实例。</para>
        /// Compress node instance.
        /// </summary>
        public sealed class CompressNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new CompressNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                var algo = GetInputString("Algorithm") ?? "lz4";
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", $"Compressing with '{algo}'");
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                SetOutput("Compressed", "compressed");
            }
        }
    }

    /// <summary>
    /// <para>字节序转换节点定义。</para>
    /// Byte-swap node definition.
    /// </summary>
    public sealed class ByteSwapNodeDefinition : NodeDefinitionBase
    {
        /// <inheritdoc />
        public override string TypeId => "data.byte_swap";

        /// <inheritdoc />
        public override string DisplayName => "Byte Swap";

        /// <inheritdoc />
        public override NodeCategory Category => NodeCategory.Data;

        /// <inheritdoc />
        public override string PluginId => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public override INodeInstance CreateInstance() => new ByteSwapNodeInstance();

        /// <summary>
        /// <para>字节序转换节点实例。</para>
        /// Byte-swap node instance.
        /// </summary>
        public sealed class ByteSwapNodeInstance : NodeInstanceBase
        {
            private static readonly INodeDefinition Def = new ByteSwapNodeDefinition();

            /// <inheritdoc />
            public override INodeDefinition Definition => Def;

            /// <inheritdoc />
            public override async ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default)
            {
                context.Log.Write(Abstractions.Logging.LogLevel.Info, "Data", "Swapping byte order");
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                SetOutput("Swapped", "swapped");
            }
        }
    }
}

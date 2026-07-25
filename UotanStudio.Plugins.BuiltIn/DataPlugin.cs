using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Plugin;

namespace UotanStudio.Plugins.BuiltIn
{
    /// <summary>
    /// <para>内置数据处理节点插件，提供文件读取、格式转换、校验和、比较、拼接、切片、加密、压缩、字节序转换等节点定义。</para>
    /// Built-in data node plugin, providing file read, format convert, checksum, compare, concat, slice, encrypt, compress, byte-swap node definitions.
    /// </summary>
    [Plugin("uotanstudio.builtin.data",
        DisplayName = "Built-in Data Nodes",
        Version = "1.0.0",
        Author = "UotanStudio",
        Description = "File read, format convert, checksum, compare, concat, slice, encrypt, compress, byte-swap nodes.")]
    public sealed class DataPlugin : IPlugin
    {
        /// <inheritdoc />
        public string Id => "uotanstudio.builtin.data";

        /// <inheritdoc />
        public string DisplayName => "Built-in Data Nodes";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "UotanStudio";

        /// <inheritdoc />
        public string Description => "File read, format convert, checksum, compare, concat, slice, encrypt, compress, byte-swap nodes.";

        /// <inheritdoc />
        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.RegisterNodeDefinition(new Data.ReadFileNodeDefinition());
            context.RegisterNodeDefinition(new Data.ConvertNodeDefinition());
            context.RegisterNodeDefinition(new Data.ChecksumNodeDefinition());
            context.RegisterNodeDefinition(new Data.CompareNodeDefinition());
            context.RegisterNodeDefinition(new Data.ConcatNodeDefinition());
            context.RegisterNodeDefinition(new Data.SliceNodeDefinition());
            context.RegisterNodeDefinition(new Data.EncryptNodeDefinition());
            context.RegisterNodeDefinition(new Data.CompressNodeDefinition());
            context.RegisterNodeDefinition(new Data.ByteSwapNodeDefinition());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}

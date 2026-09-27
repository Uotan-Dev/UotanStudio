using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>解密节点视图模型：支持 AES-CBC 与 XOR 两种解密算法，与加密节点输出格式对称。</para>
/// Decrypt node view model supporting AES-CBC and XOR decryption, symmetric with the encrypt node's output format.
/// </summary>
public class DecryptNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的解密节点。</para>
    /// Creates a decrypt node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public DecryptNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// <para>创建默认的数据解密节点，解密输入数据。</para>
    /// Creates a default data decrypt node that decrypts the input data.
    /// </summary>
    public static DecryptNodeViewModel CreateDecryptNode()
    {
        var node = new DecryptNodeViewModel("Decrypt")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Cipher.Decrypt"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Key", PortType = NodePortType.Data, IsRequired = true, IsSensitive = true });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Algorithm",
            PortType = NodePortType.Text,
            InitialValue = "aes",
            Choices = new[] { "aes", "aes-256", "xor" }.Select(value => new NodePortChoice(value)).ToArray()
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的解密逻辑：Data.Cipher.Decrypt 解密输入数据。</para>
    /// Dispatches decryption logic by the node type key (TypeKey): Data.Cipher.Decrypt decrypts the input data.
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
        switch (TypeKey)
        {
            case "Data.Cipher.Decrypt":
                ExecuteDecrypt(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteDecrypt(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Data"));
        if (data == null || data.Length == 0)
        {
            throw new InvalidDataException("No encrypted data was provided.");
        }

        var key = NodeDataUtil.ToBytes(ReadInput("Key"));
        if (key is not { Length: > 0 })
        {
            throw new InvalidDataException("A nonempty decryption key is required.");
        }
        var algorithm = (Convert.ToString(ReadInput("Algorithm"), CultureInfo.InvariantCulture) ?? "aes").Trim().ToLowerInvariant();

        byte[]? plain = algorithm switch
        {
            "aes" or "aes-256" => AesDecrypt(data, key),
            "xor" => XorDecrypt(data, key),
            _ => throw new InvalidDataException($"Unsupported cipher algorithm '{algorithm}'.")
        };
        if (plain == null)
        {
            throw new InvalidDataException($"Invalid {algorithm} ciphertext.");
        }

        WriteOutput("Data", plain);
        context.Log.Write(LogLevel.Info, Title, $"Decrypted {data.Length} bytes with {algorithm} to {plain.Length} bytes.  已解密。");
    }

    /// <summary>
    /// <para>AES-CBC 解密：密文前 BlockSize/8 字节为 IV，其后为密文主体（与加密节点的 [.. aes.IV, .. encrypted] 格式对称），
    /// 用 SHA256(key) 派生密钥。</para>
    /// AES-CBC decryption: the first BlockSize/8 bytes are the IV and the remainder the ciphertext (symmetric with the
    /// encrypt node's [.. aes.IV, .. encrypted] layout), deriving the key via SHA256(key).
    /// </summary>
    private static byte[]? AesDecrypt(byte[] data, byte[] keyMaterial)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(keyMaterial);
        var ivLength = aes.BlockSize / 8;
        if (data.Length <= ivLength)
        {
            return null;
        }

        var iv = new byte[ivLength];
        Buffer.BlockCopy(data, 0, iv, 0, ivLength);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, ivLength, data.Length - ivLength);
    }

    /// <summary>
    /// <para>XOR 解密：用 SHA256(key) 派生密钥流逐字节异或（与加密操作对称，异或可逆）。</para>
    /// XOR decryption: XORs each byte with a SHA256(key)-derived key stream (symmetric with encryption, XOR is reversible).
    /// </summary>
    private static byte[] XorDecrypt(byte[] data, byte[] keyMaterial)
    {
        var key = SHA256.HashData(keyMaterial);
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }

        return result;
    }
}
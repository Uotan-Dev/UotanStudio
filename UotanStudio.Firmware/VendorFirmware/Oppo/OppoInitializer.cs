using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// One-time bootstrap that registers Oppo parsers, crypto providers and format detectors.
/// <para>一次性引导：注册 Oppo 解析器、加密提供者与格式检测器。</para>
/// </summary>
internal static class OppoInitializer
{
    private static readonly Lock Lock = new();
    private static bool _initialized;

    /// <summary>Aggregate crypto provider that the format parsers receive as their decryption backend. <para>聚合加密提供者，作为格式解析器的解密后端传入。</para></summary>
    /// <value>A process-wide <see cref="OppCryptoProvider"/> instance; per-format providers are registered into it by <see cref="EnsureInitialized"/>. <para>进程级的 <see cref="OppCryptoProvider"/> 实例；各格式提供者由 <see cref="EnsureInitialized"/> 注册到其中。</para></value>
    public static OppCryptoProvider CryptoProvider { get; } = new OppCryptoProvider(NullOppLogger.Instance);

    /// <summary>
    /// Ensures parsers, crypto providers, and format detectors for all Oppo formats are registered.
    /// <para>确保各 Oppo 格式的解析器、加密提供者和格式检测器均已注册。</para>
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }


            var qcProvider = new OfpQcCryptoProvider(NullOppLogger.Instance);
            var mtkProvider = new OfpMtkCryptoProvider(NullOppLogger.Instance);
            var opsProvider = new OpsFormatCryptoProvider();
            var ozipProvider = new OzipFormatCryptoProvider();

            CryptoProvider.RegisterProvider(qcProvider);
            CryptoProvider.RegisterProvider(mtkProvider);
            CryptoProvider.RegisterProvider(opsProvider);
            CryptoProvider.RegisterProvider(ozipProvider);


            ParserRegistry.Register(new OfpQcFormatParser(qcProvider));
            ParserRegistry.Register(new OfpMtkFormatParser(mtkProvider));
            ParserRegistry.Register(new OpsFormatParser());
            ParserRegistry.Register(new OzipFormatParser());


            FormatDetector.RegisterDetector(TryDetectMtkFormat);

            _initialized = true;
        }
    }

    /// <summary>
    /// Attempts to detect MTK obfuscated header format using the MtkShuffle key.
    /// <para>使用 MtkShuffle 密钥尝试检测 MTK 混淆头格式。</para>
    /// </summary>
    private static OppFormat? TryDetectMtkFormat(Stream stream)
    {
        try
        {
            var fileSize = stream.Length;
            if (fileSize < OfpMtkHeader.Size)
            {
                return null;
            }

            var originalPosition = stream.Position;
            try
            {


                stream.Position = 0;
                if (stream.Length >= 4)
                {
                    Span<byte> head = stackalloc byte[4];
                    if (stream.Read(head) == 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
                    {
                        return null;
                    }
                }

                stream.Position = fileSize - OfpMtkHeader.Size;
                var headerBuffer = new byte[OfpMtkHeader.Size];
                var bytesRead = stream.Read(headerBuffer, 0, OfpMtkHeader.Size);
                if (bytesRead != OfpMtkHeader.Size)
                {
                    return null;
                }


                if (FormatDetector.IsAllZeros(headerBuffer.AsSpan(0, 46)))
                {
                    return null;
                }


                OppMtkKeyDerivation.MtkShuffle(MtkKeyDatabase.MtkHeaderShuffleKey, headerBuffer);


                if (FormatDetector.IsPrintableAscii(headerBuffer.AsSpan(0, 46)))
                {
                    return OppFormat.OfpMtk;
                }


                return FormatDetector.IsAllZeros(headerBuffer.AsSpan(0, 46)) ? OppFormat.OfpMtk : null;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }
        catch
        {
            return null;
        }
    }
}
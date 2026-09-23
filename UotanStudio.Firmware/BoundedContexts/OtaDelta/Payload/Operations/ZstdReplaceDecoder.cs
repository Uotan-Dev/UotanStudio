using System.Buffers;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Operations;

internal static class ZstdReplaceDecoder
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumWindowLog = 26;

    internal static void Decode(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var inputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var decompressor = new Decompressor();
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaximumWindowLog);
            var inputOffset = 0;
            var inputLength = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inputOffset == inputLength)
                {
                    inputLength = input.Read(inputBuffer, 0, BufferSize);
                    inputOffset = 0;
                    if (inputLength == 0)
                    {
                        throw new PartitionExtractionException("Zstd REPLACE frame is truncated.", string.Empty);
                    }
                }

                var status = decompressor.UnwrapStream(
                    inputBuffer.AsSpan(inputOffset, inputLength - inputOffset),
                    outputBuffer,
                    out var consumed,
                    out var written);
                inputOffset = checked(inputOffset + consumed);
                cancellationToken.ThrowIfCancellationRequested();
                if (written > 0)
                {
                    output.Write(outputBuffer, 0, written);
                }

                switch (status)
                {
                    case OperationStatus.Done:
                        if (inputOffset != inputLength || input.ReadByte() != -1)
                        {
                            throw new PartitionExtractionException(
                                "Zstd REPLACE accepts exactly one frame without trailing data.", string.Empty);
                        }
                        return;
                    case OperationStatus.NeedMoreData:
                    case OperationStatus.DestinationTooSmall:
                        break;
                    case OperationStatus.InvalidData:
                        throw new PartitionExtractionException(
                            "Invalid Zstd REPLACE frame. External dictionaries are not supported.", string.Empty);
                    default:
                        throw new PartitionExtractionException($"Unexpected Zstd decoder status: {status}.", string.Empty);
                }
            }
        }
        catch (ZstdException ex)
        {
            throw new PartitionExtractionException(
                "Zstd REPLACE frame exceeds the 64 MiB window limit or is invalid.", string.Empty, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }
}
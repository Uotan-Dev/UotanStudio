using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private static void WriteSignedPayload(Stream output, FileStream data, DeltaArchiveManifest manifest, RSA key, CancellationToken token)
    {
        var hashAlgorithm = HashAlgorithmName.SHA256;
        var signatureSize = SerializeSignature(new byte[key.KeySize / 8]).Length;
        manifest.SignaturesOffset = checked((ulong)data.Length);
        manifest.SignaturesSize = checked((ulong)signatureSize);
        if (manifest.CalculateSize() > 64 * 1024 * 1024) throw new PayloadGenerationException("Payload manifest exceeds 64 MiB.");
        var manifestBytes = manifest.ToByteArray();
        if (data.Length == 0)
        {
            using var encoded = new MemoryStream();
            encoded.Write(manifestBytes);
            encoded.Write([0x20, 0x00]);
            manifestBytes = encoded.ToArray();
        }
        var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, checked((uint)signatureSize));
        using var hash = Sha256.CreateAccumulator();
        hash.Append(header);
        hash.Append(manifestBytes);
        var metadataSignature = SerializeSignature(key.SignHash(hash.GetCurrentHash(), hashAlgorithm, RSASignaturePadding.Pkcs1));
        token.ThrowIfCancellationRequested();
        output.Write(header);
        output.Write(manifestBytes);
        output.Write(metadataSignature);
        data.Position = 0;
        var buffer = new byte[65536];
        long remaining = data.Length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = data.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) throw new EndOfStreamException();
            hash.Append(buffer.AsSpan(0, count));
            output.Write(buffer, 0, count);
            remaining -= count;
        }
        var payloadSignature = SerializeSignature(key.SignHash(hash.GetHashAndReset(), hashAlgorithm, RSASignaturePadding.Pkcs1));
        token.ThrowIfCancellationRequested();
        output.Write(payloadSignature);
    }

    private static byte[] SerializeSignature(byte[] signature)
    {
        using var nested = new MemoryStream();
        using (var writer = new CodedOutputStream(nested, leaveOpen: true))
        {
            writer.WriteTag(2, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(signature));
            writer.WriteTag(3, WireFormat.WireType.Fixed32);
            writer.WriteFixed32((uint)signature.Length);
        }
        using var result = new MemoryStream();
        using (var writer = new CodedOutputStream(result, leaveOpen: true))
        {
            writer.WriteTag(1, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(nested.ToArray()));
        }
        return result.ToArray();
    }
}
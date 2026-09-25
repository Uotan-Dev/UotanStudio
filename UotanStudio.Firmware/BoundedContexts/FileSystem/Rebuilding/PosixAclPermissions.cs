using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Applies Linux chmod masking to serialized access ACLs without changing named identities.</summary>
internal static class PosixAclPermissions
{
    internal static byte[] Apply(byte[] source, ushort mode)
    {
        if (source.Length is < 4 or > 65536 || mode > 0xFFF) { throw new InvalidDataException("Invalid access ACL size or mode."); }
        var version = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (version is not (1 or 2)) { throw new InvalidDataException("Unsupported access ACL version."); }
        var result = source.ToArray();
        var owner = -1;
        var group = -1;
        var mask = -1;
        var other = -1;
        ushort previousTag = 0;
        uint previousId = 0;
        var named = false;
        for (var offset = 4; offset < result.Length;)
        {
            if (result.Length - offset < 4) { throw new InvalidDataException("Truncated access ACL entry."); }
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(offset));
            var permissions = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(offset + 2));
            var identified = tag is 2 or 8;
            var size = version == 2 || identified ? 8 : 4;
            if (tag is not (1 or 2 or 4 or 8 or 16 or 32) || permissions > 7 || size > result.Length - offset
                || tag < previousTag || tag == previousTag && !identified)
            {
                throw new InvalidDataException("Invalid access ACL entry or ordering.");
            }
            if (identified)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset + 4));
                if (id == uint.MaxValue || tag == previousTag && id <= previousId)
                {
                    throw new InvalidDataException("Invalid or duplicate access ACL identity.");
                }
                previousId = id;
                named = true;
            }
            switch (tag)
            {
                case 1: owner = offset + 2; break;
                case 4: group = offset + 2; break;
                case 16: mask = offset + 2; break;
                case 32: other = offset + 2; break;
            }
            previousTag = tag;
            offset += size;
        }
        if (owner < 0 || group < 0 || other < 0 || named && mask < 0)
        {
            throw new InvalidDataException("Access ACL lacks required entries.");
        }
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(owner), (ushort)((mode >> 6) & 7));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(mask >= 0 ? mask : group), (ushort)((mode >> 3) & 7));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(other), (ushort)(mode & 7));
        return result;
    }
}
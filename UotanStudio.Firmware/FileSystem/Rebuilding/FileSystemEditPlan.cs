using System.Buffers.Binary;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Resolves image paths and inherited Linux attributes independently of host filesystem metadata.</summary>
internal sealed class FileSystemEditPlan
{
    internal const uint Ext4SemanticFlagMask = 0x600304F8u;
    internal const uint F2fsSemanticFlagMask = 0x200114F8u;

    private static readonly AsyncLocal<FileSystemEditPlan?> Active = new();
    private readonly Dictionary<string, PreservedInodeEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> hostPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> contentSources = new(StringComparer.Ordinal);
    internal string Root { get; }
    internal static FileSystemEditPlan? Current => Active.Value;
    internal IReadOnlyDictionary<string, PreservedInodeEntry> Entries => entries;
    internal IEnumerable<(string HostPath, PreservedInodeEntry Entry)> Files =>
        hostPaths.Select(pair => (Path.Combine(Root, pair.Key.Replace('/', Path.DirectorySeparatorChar)), entries[pair.Value]));

    internal FileSystemEditPlan(string root, FileSystemPreservationManifest manifest, Dictionary<string, string> projection, string? partition = null)
    {
        Root = Path.GetFullPath(root);
        var originals = manifest.ParsedEntries.ToDictionary(e => Normalize(e.Path), StringComparer.Ordinal);
        if (!originals.TryGetValue("", out var rootEntry))
            throw new InvalidDataException("Root inode attributes are missing. Re-extract the source image before editing.");
        SelinuxFileLabelInference? selinux = null;
        var preservedRoot = Copy(rootEntry, "", rootEntry.Mode);
        ValidateAttributes(preservedRoot);
        entries.Add("", preservedRoot);
        hostPaths.Add("", "");
        var siblingGroups = originals.Values.Where(e => Normalize(e.Path) != "")
            .GroupBy(e => (Parent(Normalize(e.Path)), e.Mode & 0xF000))
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray());
        var extensionDonors = originals.Values.Where(e => Normalize(e.Path) != "")
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .GroupBy(e => (Parent(Normalize(e.Path)), e.Mode & 0xF000, Path.GetExtension(e.Path)))
            .ToDictionary(g => g.Key, g => g.First());
        var originalHosts = manifest.ImagePaths.ToDictionary(p => Normalize(p.Value), p => Normalize(p.Key), StringComparer.Ordinal);
        var encodedNames = manifest.ImagePaths.Where(p => Name(p.Key) != Name(p.Value))
            .GroupBy(p => Name(p.Value), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => Name(p.Key)).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        foreach (var pair in projection.OrderBy(p => p.Key.Count(c => c == '/')).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value.StartsWith("link:", StringComparison.Ordinal))
                throw new InvalidDataException($"New host symbolic links are not supported: {pair.Key}");
            var host = pair.Key;
            var image = originalHosts.TryGetValue(host, out var mapped) ? mapped
                : Join(hostPaths[Parent(host)], encodedNames.GetValueOrDefault(Name(host), Name(host)));
            var directory = pair.Value.StartsWith("directory", StringComparison.Ordinal);
            PreservedInodeEntry selected;
            if (originals.TryGetValue(image, out var original) && ((original.Mode & 0xF000) == 0x4000) == directory)
            {
                selected = Copy(original, image, original.Mode);
                if ((original.Mode & 0xF000) is not (0x4000 or 0x8000)
                    && (!manifest.Projection.TryGetValue(host, out var before) || before != pair.Value))
                    throw new InvalidDataException($"Only regular files and directories can be edited: {image}");
            }
            else
            {
                var type = directory ? 0x4000 : 0x8000;
                var parent = Parent(image);
                PreservedInodeEntry? donor = null;
                while (true)
                {
                    donor = extensionDonors.GetValueOrDefault((parent, type, Path.GetExtension(image)))
                        ?? siblingGroups.GetValueOrDefault((parent, type), []).FirstOrDefault();
                    if (donor is not null) break;
                    if (originals.TryGetValue(parent, out donor)) break;
                    if (parent.Length == 0) break;
                    parent = Parent(parent);
                }
                donor ??= rootEntry;
                var permissions = donor.Mode & 0x0FFF;
                if (!directory && (donor.Mode & 0xF000) == 0x4000) permissions &= ~0xE49;
                selected = Copy(donor, image, (ushort)(type | permissions));
                if (!directory) selected.Flags &= ~0x40000000u;
                selected.Inode = 0;
                selected.Metadata = null;
                selected.SymlinkTarget = null;
                var executable = !directory && IsExecutable(Path.Combine(Root, host.Replace('/', Path.DirectorySeparatorChar)));
                if ((donor.Mode & 0xF000) == 0x4000) InheritDirectoryAcl(selected, directory, executable);
                if (executable && (selected.Mode & 0x49) == 0)
                {
                    selected.Mode |= (ushort)((selected.Mode & 0x124) >> 2);
                    if (selected.Xattrs?.TryGetValue("system.posix_acl_access", out var acl) == true) AddAclExecuteBits(acl);
                }
                if (!directory) selected.Xattrs?.Remove("system.posix_acl_default");
                selected.Xattrs?.Remove("security.selinux");
                selinux ??= new SelinuxFileLabelInference(Root, originals, projection, partition, manifest.ImagePaths);
                if (selinux.Infer(image, directory, executable) is { } inferredLabel)
                {
                    selected.Xattrs ??= new(StringComparer.Ordinal);
                    selected.Xattrs["security.selinux"] = inferredLabel;
                }
            }
            ValidateAttributes(selected);
            if (!entries.TryAdd(image, selected)) throw new InvalidDataException($"Two host paths map to the same image path: {image}");
            hostPaths.Add(host, image);
        }
        var imageHosts = hostPaths.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        foreach (var group in entries.Values.Where(e => e.Inode != 0 && (e.Mode & 0xF000) == 0x8000).GroupBy(e => e.Inode).Where(g => g.Count() > 1))
        {
            var paths = group.Select(e => imageHosts[e.Path]).ToArray();
            var changed = paths.Where(p => !manifest.Projection.TryGetValue(p, out var before) || projection[p] != before).ToArray();
            if (changed.Select(p => projection[p]).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException($"Conflicting edits to hard-linked files: {string.Join(", ", paths)}");
            var canonical = changed.FirstOrDefault() ?? paths[0];
            foreach (var path in paths) contentSources[path] = Path.Combine(Root, canonical.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    internal IReadOnlyDictionary<string, FsConfigEntry> ApplyAttributes(IReadOnlyList<FileSystemAttributeEdit> edits, CancellationToken token)
    {
        if (edits.Count > 256) { throw new InvalidDataException("Too many filesystem attribute edits."); }
        var original = entries.ToDictionary(pair => pair.Key,
            pair => (pair.Value.Uid, pair.Value.Gid, Permissions: (ushort)(pair.Value.Mode & 0xFFF)), StringComparer.Ordinal);
        var requested = new Dictionary<string, (uint Uid, uint Gid, ushort Permissions)>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentNullException.ThrowIfNull(edit.Path);
            var path = edit.Path.TrimStart('/');
            if (edit.Path.Length is 0 or > 4096 || path.Contains('\\') || path.Contains('\0')
                || path.Length > 0 && path.Split('/').Any(part => part is "" or "." or "..")
                || !targets.Add(path) || edit.Permissions > 0xFFF || edit.ExpectedPermissions is > 0xFFF)
            {
                throw new InvalidDataException("Invalid or duplicate filesystem attribute target.");
            }
            if (!entries.TryGetValue(path, out var entry)) { throw new FileNotFoundException("Attribute target is missing.", path); }
            var before = original[path];
            if (edit.ExpectedUid is { } uid && uid != before.Uid
                || edit.ExpectedGid is { } gid && gid != before.Gid
                || edit.ExpectedPermissions is { } mode && mode != before.Permissions)
            {
                throw new InvalidDataException("Filesystem attribute old-value guard failed.");
            }
            var desired = (edit.Uid, edit.Gid, edit.Permissions);
            var aliases = entry.Inode != 0 && (entry.Mode & 0xF000) == 0x8000
                ? entries.Values.Where(item => item.Inode == entry.Inode && (item.Mode & 0xF000) == 0x8000)
                : [entry];
            foreach (var alias in aliases)
            {
                token.ThrowIfCancellationRequested();
                if (requested.TryGetValue(alias.Path, out var previous) && previous != desired)
                {
                    throw new InvalidDataException("Conflicting attributes for hard-linked files.");
                }
                if ((alias.Mode & 0xFFF) != edit.Permissions && alias.Xattrs?.TryGetValue("system.posix_acl_access", out var acl) == true)
                {
                    alias.Xattrs["system.posix_acl_access"] = PosixAclPermissions.Apply(acl, edit.Permissions);
                }
                requested[alias.Path] = desired;
                alias.Uid = edit.Uid;
                alias.Gid = edit.Gid;
                alias.Mode = (ushort)((alias.Mode & 0xF000) | edit.Permissions);
            }
        }
        return requested.ToDictionary(pair => pair.Key,
            pair => new FsConfigEntry(pair.Value.Uid, pair.Value.Gid, pair.Value.Permissions), StringComparer.Ordinal);
    }

    internal void ApplyUserXattrs(IReadOnlyList<FileSystemXattrEdit> edits, CancellationToken token)
    {
        if (edits.Count > 256) { throw new InvalidDataException("Too many extended attribute edits."); }
        var requested = new Dictionary<(string Path, string Name), byte[]?>();
        var xattrOriginals = new Dictionary<(string Path, string Name), byte[]?>();
        var targets = new HashSet<(string Path, string Name)>();
        foreach (var edit in edits)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentNullException.ThrowIfNull(edit.Path);
            ArgumentNullException.ThrowIfNull(edit.Name);
            var path = edit.Path.TrimStart('/');
            if (edit.Path.Length is 0 or > 4096 || path.Contains('\\') || path.Contains('\0')
                || path.Length > 0 && path.Split('/').Any(part => part is "" or "." or "..")
                || !edit.Name.StartsWith("user.", StringComparison.Ordinal) || edit.Name.Length is <= 5 or > 255
                || edit.Name.Any(character => character is < '!' or > '~')
                || edit.Value?.Length > 65536 || edit.ExpectedValue?.Length > 65536 || !targets.Add((path, edit.Name)))
            {
                throw new InvalidDataException("Invalid or duplicate user extended attribute edit.");
            }
            if (!entries.TryGetValue(path, out var entry)) { throw new FileNotFoundException("Extended attribute target is missing.", path); }
            var current = entry.Xattrs?.GetValueOrDefault(edit.Name);
            var original = xattrOriginals.GetValueOrDefault((path, edit.Name), current);
            if (edit.CheckOldValue && !SameXattr(original, edit.ExpectedValue))
            {
                throw new InvalidDataException("Extended attribute old-value guard failed.");
            }
            var aliases = entry.Inode != 0 && (entry.Mode & 0xF000) == 0x8000
                ? entries.Values.Where(item => item.Inode == entry.Inode && (item.Mode & 0xF000) == 0x8000)
                : [entry];
            foreach (var alias in aliases)
            {
                token.ThrowIfCancellationRequested();
                var key = (alias.Path, edit.Name);
                if (requested.TryGetValue(key, out var previous) && !SameXattr(previous, edit.Value))
                {
                    throw new InvalidDataException("Conflicting extended attributes for hard-linked files.");
                }
                xattrOriginals.TryAdd(key, alias.Xattrs?.GetValueOrDefault(edit.Name));
                requested[key] = edit.Value;
                if (edit.Value is null) { alias.Xattrs?.Remove(edit.Name); }
                else
                {
                    alias.Xattrs ??= new(StringComparer.Ordinal);
                    alias.Xattrs[edit.Name] = edit.Value.ToArray();
                }
            }
        }
    }

    private static bool SameXattr(byte[]? first, byte[]? second)
        => first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);

    internal string ContentSource(string fullPath) => contentSources.GetValueOrDefault(Relative(fullPath), fullPath);
    internal bool Includes(string fullPath) => hostPaths.ContainsKey(Relative(fullPath));
    internal PreservedInodeEntry Get(string fullPath) => entries[hostPaths[Relative(fullPath)]];
    internal IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = this;
        return new Scope(() => Active.Value = previous);
    }
    private string Relative(string path) => Normalize(Path.GetRelativePath(Root, path).Replace('\\', '/'));
    internal static string Normalize(string path) => path is "." or "/" ? "" : path.Trim('/');
    internal static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Parent(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";
    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static void ValidateAttributes(PreservedInodeEntry entry)
    {
        entry.Metadata?.Validate();
        if ((entry.Flags & (0x800u | 0x100000u)) != 0)
            throw new InvalidDataException($"Encrypted or verity inode requires a dedicated semantic writer: {entry.Path}");
        if ((entry.Flags & 0x40000000u) != 0 && (entry.Mode & 0xF000) != 0x4000)
            throw new InvalidDataException($"Casefold flag belongs only on directories: {entry.Path}");
        entry.Xattrs?.Remove("system.data");
        if (entry.Xattrs?.Keys.Any(k => k is "security.ima" or "security.evm" or "security.fsverity" or "security.fscrypt" or "encryption.c") == true)
            throw new InvalidDataException($"Content authentication/encryption attributes require a dedicated writer: {entry.Path}");
    }

    private static bool IsExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (length >= 2 && header[..2].SequenceEqual("#!"u8)) return true;
        if (length < 52 || !header[..4].SequenceEqual("\u007fELF"u8) || header[5] is not (1 or 2)) return false;
        var little = header[5] == 1;
        var type = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) : BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        if (type == 2) return true;
        if (type != 3 || header[4] is not (1 or 2) || header[4] == 2 && length < 64) return false;
        var elf64 = header[4] == 2;
        var offset = elf64 ? (little ? BinaryPrimitives.ReadUInt64LittleEndian(header[32..]) : BinaryPrimitives.ReadUInt64BigEndian(header[32..]))
            : (little ? BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) : BinaryPrimitives.ReadUInt32BigEndian(header[28..]));
        var sizeOffset = elf64 ? 54 : 42;
        var size = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[sizeOffset..]) : BinaryPrimitives.ReadUInt16BigEndian(header[sizeOffset..]);
        var count = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[(sizeOffset + 2)..]) : BinaryPrimitives.ReadUInt16BigEndian(header[(sizeOffset + 2)..]);
        if (size < (elf64 ? 56 : 32) || count > 4096 || offset > (ulong)stream.Length || (ulong)count * size > (ulong)stream.Length - offset) return false;
        for (var index = 0; index < count; index++)
        {
            stream.Position = checked((long)(offset + (ulong)index * size));
            stream.ReadExactly(header[..4]);
            var kind = little ? BinaryPrimitives.ReadUInt32LittleEndian(header) : BinaryPrimitives.ReadUInt32BigEndian(header);
            if (kind == 3) return true;
        }
        return false;
    }

    private static void AddAclExecuteBits(byte[] acl)
    {
        if (acl.Length < 4) throw new InvalidDataException("Truncated executable ACL.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(acl);
        if (version is not (1 or 2)) throw new InvalidDataException("Unsupported executable ACL version.");
        for (var offset = 4; offset < acl.Length;)
        {
            if (acl.Length - offset < 4) throw new InvalidDataException("Truncated executable ACL entry.");
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(acl.AsSpan(offset));
            var permissions = BinaryPrimitives.ReadUInt16LittleEndian(acl.AsSpan(offset + 2));
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(offset + 2), (ushort)(permissions | ((permissions & 4) >> 2)));
            var size = version == 2 || tag is 2 or 8 ? 8 : 4;
            if (size > acl.Length - offset) throw new InvalidDataException("Truncated executable ACL identity.");
            offset += size;
        }
    }

    private static void InheritDirectoryAcl(PreservedInodeEntry entry, bool directory, bool executable)
    {
        if (entry.Xattrs is not { } attributes) return;
        if (attributes.TryGetValue("system.posix_acl_default", out var defaults))
            attributes["system.posix_acl_access"] = defaults.ToArray();
        if (!attributes.TryGetValue("system.posix_acl_access", out var acl)) return;
        if (acl.Length < 4) throw new InvalidDataException("Truncated inherited POSIX ACL.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(acl);
        if (version is not (1 or 2)) throw new InvalidDataException("Unsupported inherited POSIX ACL version.");
        int? owner = null, group = null, mask = null, other = null;
        for (var offset = 4; offset < acl.Length;)
        {
            if (acl.Length - offset < 4) throw new InvalidDataException("Truncated inherited ACL entry.");
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(acl.AsSpan(offset));
            var permissions = BinaryPrimitives.ReadUInt16LittleEndian(acl.AsSpan(offset + 2));
            if (!directory && !executable) permissions &= 6;
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(offset + 2), permissions);
            switch (tag) { case 1: owner = permissions; break; case 4: group = permissions; break; case 16: mask = permissions; break; case 32: other = permissions; break; }
            var size = version == 2 || tag is 2 or 8 ? 8 : 4;
            if (size > acl.Length - offset) throw new InvalidDataException("Truncated inherited ACL identity.");
            offset += size;
        }
        if (owner is null || group is null || other is null) throw new InvalidDataException("Inherited ACL lacks required owner/group/other entries.");
        entry.Mode = (ushort)((entry.Mode & 0xFE00) | (owner.Value << 6) | ((mask ?? group.Value) << 3) | other.Value);
    }
    private static PreservedInodeEntry Copy(PreservedInodeEntry source, string path, ushort mode) => new()
    {
        Path = path,
        Size = source.Size,
        Inode = source.Inode,
        DeviceId = source.DeviceId,
        Flags = source.Flags,
        Mode = mode,
        Uid = source.Uid,
        Gid = source.Gid,
        Metadata = source.Metadata,
        SymlinkTarget = source.SymlinkTarget,
        Xattrs = source.Xattrs?.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal)
    };
    private sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
}
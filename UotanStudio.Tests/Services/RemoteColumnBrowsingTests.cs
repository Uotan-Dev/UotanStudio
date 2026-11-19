using UotanStudio.Services;
using UotanStudio.Services.Remote;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RemoteColumnBrowsingTests
{
    private sealed class FakeRemoteFileSystem : IRemoteFileSystem
    {
        private readonly Dictionary<string, IReadOnlyList<RemoteFileEntry>> _directories;

        public FakeRemoteFileSystem(Dictionary<string, IReadOnlyList<RemoteFileEntry>> directories, RemoteProtocol protocol = RemoteProtocol.Sftp)
        {
            _directories = directories;
            Protocol = protocol;
        }

        public RemoteProtocol Protocol { get; }

        public string DisplayName => "fake://remote";

        public int DisposedCount { get; private set; }

        public List<string> EnumeratedPaths { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<RemoteFileEntry>> EnumerateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
        {
            EnumeratedPaths.Add(directoryPath);
            IReadOnlyList<RemoteFileEntry> entries = _directories.TryGetValue(directoryPath, out var found) ? found : [];
            return Task.FromResult(entries);
        }

        public Task<RemoteFileEntry?> TryGetEntryAsync(string remotePath, CancellationToken cancellationToken)
            => Task.FromResult<RemoteFileEntry?>(null);

        public Task DownloadAsync(string remotePath, string localDestination, CancellationToken cancellationToken)
        {
            Calls.Add($"download:{remotePath}->{localDestination}");
            return Task.CompletedTask;
        }

        public Task<string> DownloadToTempAsync(string remoteFilePath, CancellationToken cancellationToken)
        {
            Calls.Add($"downloadToTemp:{remoteFilePath}");
            return Task.FromResult(remoteFilePath);
        }

        public Task UploadAsync(string localPath, string remoteDestination, CancellationToken cancellationToken)
        {
            Calls.Add($"upload:{localPath}->{remoteDestination}");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken)
        {
            Calls.Add($"delete:{remotePath}:{isDirectory}");
            return Task.CompletedTask;
        }

        public Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken)
        {
            Calls.Add($"rename:{remotePath}->{newName}");
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
        {
            Calls.Add($"mkdir:{directoryPath}");
            return Task.CompletedTask;
        }

        public void Dispose() => DisposedCount++;
    }

    private static RemoteConnectionProfile CreateProfile(string initialPath = "/") => new()
    {
        Id = "test-" + Guid.NewGuid().ToString("N"),
        Protocol = RemoteProtocol.Sftp,
        Host = "fake.local",
        Port = 22,
        Username = "tester",
        Encryption = RemoteEncryptionMode.Ssh,
        InitialPath = initialPath,
    };

    [Fact]
    public async Task LoadRemotePathAsync_DirectoryEntries_BecomeBrowseRows()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>>
        {
            ["/"] =
            [
                new RemoteFileEntry { Name = "etc", FullPath = "/etc", IsDirectory = true },
                new RemoteFileEntry { Name = "boot.img", FullPath = "/boot.img", IsDirectory = false, Size = 4096 },
            ],
        });

        var service = RemoteFileSystemService.Instance;
        var previous = service.SessionFactoryOverride;
        service.SessionFactoryOverride = _ => fake;
        try
        {
            var document = new CompareViewModel();
            var added = await document.AddRemotePathAsync(CreateProfile(), "/");

            Assert.True(added);
            var column = document.Columns.First(candidate => candidate.IsRemoteColumn);
            Assert.Equal("/", column.Path);
            Assert.NotNull(column.TreeRoot);
            Assert.NotNull(column.Rows);
            Assert.Equal(2, column.Rows!.Count);
            Assert.Contains("/", fake.EnumeratedPaths);
        }
        finally
        {
            service.SessionFactoryOverride = previous;
        }
    }

    [Fact]
    public async Task LoadRemotePathAsync_InitialPathOmitted_UsesProfilePath()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>>
        {
            ["/sdcard"] = [],
        });

        var service = RemoteFileSystemService.Instance;
        var previous = service.SessionFactoryOverride;
        service.SessionFactoryOverride = _ => fake;
        try
        {
            var document = new CompareViewModel();
            await document.AddRemotePathAsync(CreateProfile("/sdcard"), null);

            var column = document.Columns.First(candidate => candidate.IsRemoteColumn);
            Assert.Equal("/sdcard", column.Path);
            Assert.Contains("/sdcard", fake.EnumeratedPaths);
        }
        finally
        {
            service.SessionFactoryOverride = previous;
        }
    }

    [Fact]
    public async Task AddRemotePathAsync_WhileComparing_ReportsErrorAndAddsNothing()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>>());
        var service = RemoteFileSystemService.Instance;
        var previous = service.SessionFactoryOverride;
        service.SessionFactoryOverride = _ => fake;
        try
        {
            var document = new CompareViewModel();
            await document.OpenCompareFoldersAsync(Path.GetTempPath(), Path.GetTempPath());
            var before = document.Columns.Count;

            var added = await document.AddRemotePathAsync(CreateProfile(), "/");

            Assert.False(added);
            Assert.True(document.HasError);
            Assert.Equal(before, document.Columns.Count);
        }
        finally
        {
            service.SessionFactoryOverride = previous;
        }
    }

    [Fact]
    public async Task Dispose_ClosesRemoteSession()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var service = RemoteFileSystemService.Instance;
        var previous = service.SessionFactoryOverride;
        service.SessionFactoryOverride = _ => fake;
        try
        {
            var document = new CompareViewModel();
            await document.AddRemotePathAsync(CreateProfile(), "/");
            var column = document.Columns.First(candidate => candidate.IsRemoteColumn);

            column.Dispose();

            Assert.Equal(1, fake.DisposedCount);
        }
        finally
        {
            service.SessionFactoryOverride = previous;
        }
    }

    [Fact]
    public async Task DeleteRemoteEntryAsync_DelegatesToSession()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");

        await column.DeleteRemoteEntryAsync("/etc/hosts", isDirectory: false);

        Assert.Contains("delete:/etc/hosts:False", fake.Calls);
    }

    [Fact]
    public async Task RenameRemoteEntryAsync_KeepsEntryInSameParent()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");

        await column.RenameRemoteEntryAsync("/etc/hosts", "hosts.bak");

        Assert.Contains("rename:/etc/hosts->hosts.bak", fake.Calls);
    }

    [Fact]
    public async Task CreateRemoteDirectoryAsync_UsesEntryFolderWhenEntryIsDirectory()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");

        await column.CreateRemoteDirectoryAsync("/data", isDirectory: true, folderName: "backup");

        Assert.Contains("mkdir:/data/backup", fake.Calls);
    }

    [Fact]
    public async Task CreateRemoteDirectoryAsync_UsesParentWhenEntryIsFile()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");

        await column.CreateRemoteDirectoryAsync("/data/config.txt", isDirectory: false, folderName: "backup");

        Assert.Contains("mkdir:/data/backup", fake.Calls);
    }

    [Fact]
    public async Task UploadToRemoteAsync_ForwardsLocalPathAndDestination()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");

        await column.UploadToRemoteAsync("boot.img", "/uploads/");

        Assert.Contains("upload:boot.img->/uploads/", fake.Calls);
    }

    [Fact]
    public async Task DownloadFromRemoteAsync_ExistingDirectoryReceivesEntryName()
    {
        var fake = new FakeRemoteFileSystem(new Dictionary<string, IReadOnlyList<RemoteFileEntry>> { ["/"] = [] });
        var column = await LoadColumnAsync(fake, "/");
        var localDirectory = Directory.CreateTempSubdirectory("uotan-remote-test-").FullName;
        try
        {
            await column.DownloadFromRemoteAsync("/etc/hosts", localDirectory);

            var expected = Path.Combine(localDirectory, "hosts");
            Assert.Contains($"download:/etc/hosts->{expected}", fake.Calls);
        }
        finally
        {
            Directory.Delete(localDirectory, recursive: true);
        }
    }

    private static async Task<CompareColumnViewModel> LoadColumnAsync(FakeRemoteFileSystem fake, string path)
    {
        var service = RemoteFileSystemService.Instance;
        var previous = service.SessionFactoryOverride;
        service.SessionFactoryOverride = _ => fake;
        try
        {
            var document = new CompareViewModel();
            await document.AddRemotePathAsync(CreateProfile(path), path);
            return document.Columns.First(candidate => candidate.IsRemoteColumn);
        }
        finally
        {
            service.SessionFactoryOverride = previous;
        }
    }
}
using UotanStudio.Services;
using UotanStudio.Services.Remote;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RemoteFileSystemPathsTests
{
    [Fact]
    public void CombinePath_RootParent_ProducesSingleSlash()
    {
        Assert.Equal("/etc", RemoteFileSystemService.CombinePath("/", "etc"));
        Assert.Equal("/etc/hosts", RemoteFileSystemService.CombinePath("/etc", "hosts"));
        Assert.Equal("/etc/hosts", RemoteFileSystemService.CombinePath("/etc/", "hosts"));
    }

    [Fact]
    public void GetParentPath_RootAndNested_ReturnsExpected()
    {
        Assert.Equal("/", RemoteFileSystemService.GetParentPath("/"));
        Assert.Equal("/", RemoteFileSystemService.GetParentPath("/etc"));
        Assert.Equal("/etc", RemoteFileSystemService.GetParentPath("/etc/hosts"));
        Assert.Equal("/a/b", RemoteFileSystemService.GetParentPath("/a/b/c"));
        Assert.Equal("/a/b", RemoteFileSystemService.GetParentPath("/a/b/c/"));
    }

    [Fact]
    public void GetEntryName_ExtractsLastSegment()
    {
        Assert.Equal(string.Empty, RemoteFileSystemService.GetEntryName("/"));
        Assert.Equal("etc", RemoteFileSystemService.GetEntryName("/etc"));
        Assert.Equal("hosts", RemoteFileSystemService.GetEntryName("/etc/hosts"));
        Assert.Equal("hosts", RemoteFileSystemService.GetEntryName("/etc/hosts/"));
    }

    [Fact]
    public void GetDefaultPath_EmptyInitialPath_FallsBackToRoot()
    {
        var profile = new RemoteConnectionProfile { InitialPath = string.Empty };
        Assert.Equal("/", RemoteFileSystemService.GetDefaultPath(profile));

        profile.InitialPath = "  /sdcard  ";
        Assert.Equal("/sdcard", RemoteFileSystemService.GetDefaultPath(profile));
    }

    [Fact]
    public void CreateSession_FtpProfile_ReturnsFtpSession()
    {
        var profile = new RemoteConnectionProfile
        {
            Protocol = RemoteProtocol.Ftp,
            Host = "127.0.0.1",
            Port = 21,
            Encryption = RemoteEncryptionMode.None,
        };

        using var session = RemoteFileSystemService.Instance.CreateSession(profile);
        Assert.Equal(RemoteProtocol.Ftp, session.Protocol);
        Assert.Contains("127.0.0.1:21", session.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSession_WebDavProfile_RejectsInvalidHostWithoutConnecting()
    {
        var profile = new RemoteConnectionProfile
        {
            Protocol = RemoteProtocol.WebDav,
            Host = "localhost",
            Port = 8080,
            Encryption = RemoteEncryptionMode.None,
        };

        using var session = RemoteFileSystemService.Instance.CreateSession(profile);
        Assert.Equal(RemoteProtocol.WebDav, session.Protocol);
        Assert.Contains("http://localhost:8080", session.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSession_SftpAndScpProfiles_ProduceSshSessions()
    {
        var sftp = new RemoteConnectionProfile { Protocol = RemoteProtocol.Sftp, Host = "localhost", Port = 22 };
        var scp = new RemoteConnectionProfile { Protocol = RemoteProtocol.Scp, Host = "localhost", Port = 22 };

        using var sftpSession = RemoteFileSystemService.Instance.CreateSession(sftp);
        using var scpSession = RemoteFileSystemService.Instance.CreateSession(scp);
        Assert.Equal(RemoteProtocol.Sftp, sftpSession.Protocol);
        Assert.Equal(RemoteProtocol.Scp, scpSession.Protocol);
        Assert.StartsWith("sftp://", sftpSession.DisplayName, StringComparison.Ordinal);
        Assert.StartsWith("scp://", scpSession.DisplayName, StringComparison.Ordinal);
    }
}
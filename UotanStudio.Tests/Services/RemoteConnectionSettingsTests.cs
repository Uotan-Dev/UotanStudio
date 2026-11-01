using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RemoteConnectionSettingsTests
{
    [Fact]
    public void RemoteConnectionProfile_JsonSerialization_RoundTripsCorrectly()
    {
        var settings = new AppSettings();
        settings.RemoteConnectionHistory.Add(new RemoteConnectionProfile
        {
            Id = "test1",
            Protocol = RemoteProtocol.Sftp,
            Encryption = RemoteEncryptionMode.Ssh,
            Host = "192.168.1.100",
            Port = 22,
            Username = "root",
            Password = "secretpassword",
            IsAnonymous = false,
            LastConnected = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)
        });
        settings.RemoteConnectionHistory.Add(new RemoteConnectionProfile
        {
            Id = "test2",
            Protocol = RemoteProtocol.Ftp,
            Encryption = RemoteEncryptionMode.TlsExplicit,
            Host = "ftp.example.com",
            Port = 21,
            Username = "anonymous",
            Password = string.Empty,
            IsAnonymous = true,
            LastConnected = null
        });

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.NotNull(json);

        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.RemoteConnectionHistory.Count);

        var first = reloaded.RemoteConnectionHistory[0];
        Assert.Equal("test1", first.Id);
        Assert.Equal(RemoteProtocol.Sftp, first.Protocol);
        Assert.Equal(RemoteEncryptionMode.Ssh, first.Encryption);
        Assert.Equal("192.168.1.100", first.Host);
        Assert.Equal(22, first.Port);
        Assert.Equal("root", first.Username);
        Assert.Equal("secretpassword", first.Password);
        Assert.False(first.IsAnonymous);
        Assert.NotNull(first.LastConnected);

        var second = reloaded.RemoteConnectionHistory[1];
        Assert.Equal("test2", second.Id);
        Assert.Equal(RemoteProtocol.Ftp, second.Protocol);
        Assert.Equal(RemoteEncryptionMode.TlsExplicit, second.Encryption);
        Assert.Equal("ftp.example.com", second.Host);
        Assert.Equal(21, second.Port);
        Assert.True(second.IsAnonymous);
        Assert.Null(second.LastConnected);
    }

    [Fact]
    public void RemoteConnectionViewModel_ProtocolSelection_UpdatesDefaultPortAndEncryption()
    {
        var vm = new RemoteConnectionViewModel();

        // Default protocol is SFTP
        Assert.Equal(RemoteProtocol.Sftp, vm.SelectedProtocolOption.Protocol);
        Assert.Equal(22, vm.Port);
        Assert.Equal(RemoteEncryptionMode.Ssh, vm.SelectedEncryptionOption.Mode);

        // Switch to FTP
        var ftpOption = Assert.Single(vm.Protocols, p => p.Protocol == RemoteProtocol.Ftp);
        vm.SelectedProtocolOption = ftpOption;
        Assert.Equal(21, vm.Port);
        Assert.Equal(RemoteEncryptionMode.None, vm.SelectedEncryptionOption.Mode);

        // Switch to WebDAV
        var webdavOption = Assert.Single(vm.Protocols, p => p.Protocol == RemoteProtocol.WebDav);
        vm.SelectedProtocolOption = webdavOption;
        Assert.Equal(80, vm.Port);
        Assert.Equal(RemoteEncryptionMode.None, vm.SelectedEncryptionOption.Mode);

        // Switch WebDAV encryption to HTTPS
        var httpsOption = Assert.Single(vm.AvailableEncryptions, e => e.Mode == RemoteEncryptionMode.Https);
        vm.SelectedEncryptionOption = httpsOption;
        Assert.Equal(443, vm.Port);

        // Switch back to SCP
        var scpOption = Assert.Single(vm.Protocols, p => p.Protocol == RemoteProtocol.Scp);
        vm.SelectedProtocolOption = scpOption;
        Assert.Equal(22, vm.Port);
        Assert.Equal(RemoteEncryptionMode.Ssh, vm.SelectedEncryptionOption.Mode);
    }

    [Fact]
    public void RemoteConnectionViewModel_SaveAndReconnect_UpdatesHistory()
    {
        var testHost = $"test-{Guid.NewGuid():N}.local";
        var vm = new RemoteConnectionViewModel();
        try
        {
            vm.Host = testHost;
            vm.Port = 2222;
            vm.Username = "tester";
            vm.Password = "pwd123";

            vm.SaveCommand.Execute(null);

            var historyItem = Assert.Single(vm.History, h => h.Host == testHost && h.Port == 2222);
            Assert.Equal("tester", historyItem.Username);
            Assert.Null(historyItem.LastConnected);

            // Now test Reconnect which updates LastConnected
            vm.ReconnectCommand.Execute(historyItem);
            Assert.NotNull(vm.ConnectedProfile);
            Assert.NotNull(vm.ConnectedProfile.LastConnected);

            // Test delete
            vm.DeleteCommand.Execute(historyItem);
            Assert.DoesNotContain(vm.History, h => h.Host == testHost && h.Port == 2222);
        }
        finally
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            settings.RemoteConnectionHistory.RemoveAll(h => h.Host.Contains("test-") || h.Host == "10.0.0.1");
            SettingsPersistenceService.Instance.SaveSettings(settings);
        }
    }
}
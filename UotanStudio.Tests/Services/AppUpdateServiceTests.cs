using UotanStudio.Net.GitHub;
using UotanStudio.Services;
using UotanStudio.Services.Update;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class AppUpdateServiceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static GitHubRelease Release(string version, params GitHubAsset[] assets)
        => new("Uotan-Dev", "UotanStudio", "v" + version, version, assets) { ReleaseNotes = "notes" };

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.0.0.1", "1.0.0", true)]
    public void IsNewerVersion_NumericSegments_ComparesSegmentBySegment(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppUpdateService.IsNewerVersion(candidate, current));

    [Theory]
    [InlineData("1.0.1-beta.1", "1.0.0", true)]
    [InlineData("1.0.0-beta.2", "1.0.0", false)]
    [InlineData("1.0.1-beta.1", "1.0.1", false)]
    [InlineData("1.0.1", "1.0.1-beta.1", true)]
    public void IsNewerVersion_PrereleaseSuffix_IsOlderThanItsRelease(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppUpdateService.IsNewerVersion(candidate, current));

    [Theory]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.9", true)]
    [InlineData("1.0.0-beta.9", "1.0.0-beta.10", false)]
    [InlineData("1.0.0-beta.9", "1.0.0-rc.1", false)]
    public void IsNewerVersion_PrereleaseSuffixSegments_CompareNumerically(
        string candidate, string current, bool expected)
        => Assert.Equal(expected, AppUpdateService.IsNewerVersion(candidate, current));

    [Theory]
    [InlineData("1.0.1-beta.1", true)]
    [InlineData("1.0.1", false)]
    [InlineData("v2.0.0-rc1", true)]
    public void IsPrereleaseVersion_SuffixPresent_ReportsPrerelease(string version, bool expected)
        => Assert.Equal(expected, AppUpdateService.IsPrereleaseVersion(version));

    [Fact]
    public void SelectPackage_PlatformRidInstallerAndArchive_PrefersTheInstaller()
    {
        var rid = AppUpdateService.CurrentRuntimeIdentifier();
        var release = Release("1.0.1",
            new GitHubAsset($"uotanstudio-{rid}.tar.gz", "https://example/tar", 10, Sha),
            new GitHubAsset($"uotanstudio-1.0.1-{rid}.deb", "https://example/deb", 20, Sha));

        var package = AppUpdateService.SelectPackage(release);

        Assert.NotNull(package);
        Assert.Equal($"uotanstudio-1.0.1-{rid}.deb", package.Name);
        Assert.True(package.IsInstaller);
        Assert.Equal(20, package.Size);
        Assert.Equal(Sha, package.Sha256);
    }

    [Fact]
    public void SelectPackage_NoInstaller_FallsBackToThePlainArchive()
    {
        var release = Release("1.0.1",
            new GitHubAsset("uotanstudio-win-x64.zip", "https://example/zip", 10, Sha));

        var package = AppUpdateService.SelectPackage(release);

        Assert.NotNull(package);
        Assert.Equal("uotanstudio-win-x64.zip", package.Name);
        Assert.False(package.IsInstaller);
    }

    [Fact]
    public void SelectPackage_OnlyForeignPlatformAssets_ReturnsNull()
    {
        var foreign = OperatingSystem.IsWindows()
            ? new GitHubAsset("uotanstudio-osx-arm64.dmg", "https://example/dmg", 10, Sha)
            : new GitHubAsset("uotanstudio-win-x64.zip", "https://example/zip", 10, Sha);

        Assert.Null(AppUpdateService.SelectPackage(Release("1.0.1", foreign)));
    }

    [Fact]
    public void SelectPackage_EmptyRelease_ReturnsNull()
        => Assert.Null(AppUpdateService.SelectPackage(Release("1.0.1")));

    [Fact]
    public void SelectPackage_NonUotanStudioAsset_IsIgnored()
    {
        var release = Release("1.0.1",
            new GitHubAsset("UotanStudio.cer", "https://example/cer", 1, null));

        Assert.Null(AppUpdateService.SelectPackage(release));
    }

    [Fact]
    public void ApplyResult_NewerReleaseWithPackage_MovesToUpdateAvailable()
    {
        var viewModel = CreateViewModel();
        var asset = new GitHubAsset(PackageNameForThisPlatform(), "https://example/pkg", 1024, Sha);
        var result = new AppUpdateCheckResult(Release("99.0.0", asset), true,
            AppUpdateService.SelectPackage(Release("99.0.0", asset)), "notes");

        viewModel.ApplyResult(result);

        Assert.Equal(CheckUpdateStage.UpdateAvailable, viewModel.Stage);
        Assert.True(viewModel.CanUpdate);
        Assert.True(viewModel.CanSkip);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("notes", viewModel.ReleaseNotes);
        Assert.Equal("99.0.0", viewModel.LatestVersion);
    }

    [Fact]
    public void ApplyResult_CurrentVersion_ReportsUpToDateWithoutActions()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyResult(new AppUpdateCheckResult(Release("0.0.1"), false, null, null));

        Assert.Equal(CheckUpdateStage.UpToDate, viewModel.Stage);
        Assert.False(viewModel.CanUpdate);
        Assert.False(viewModel.CanSkip);
        Assert.True(viewModel.HasResult);
    }

    [Fact]
    public void ApplyResult_NewerReleaseWithoutPackage_ReportsMissingPackage()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyResult(new AppUpdateCheckResult(Release("9.9.9"), true, null, null));

        Assert.Equal(CheckUpdateStage.Failed, viewModel.Stage);
        Assert.False(viewModel.CanUpdate);
    }

    [Fact]
    public void ResetForManualCheck_DiscardsThePreviousResultSoTheNextOpenRefetches()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyResult(new AppUpdateCheckResult(Release("9.9.9"), true, null, null));

        viewModel.ResetForManualCheck();

        Assert.False(viewModel.HasResult);
        Assert.Equal(CheckUpdateStage.Checking, viewModel.Stage);
        Assert.False(viewModel.CanUpdate);
        Assert.Equal(string.Empty, viewModel.ReleaseNotes);
    }

    [Fact]
    public async Task UpdateAsync_WithoutACheckedResult_DoesNothing()
        => Assert.False(await CreateViewModel().UpdateAsync());

    [Fact]
    public void Incremental_CheckStateTransitionsStayConsistent()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyResult(new AppUpdateCheckResult(Release("5.0.0"), true, null, null));
        Assert.False(viewModel.ShowsProgress);
        Assert.True(viewModel.ShowsReleaseNotes);

        viewModel.ResetForManualCheck();
        Assert.False(viewModel.ShowsReleaseNotes);
    }

    [Fact]
    public void SkipVersion_RecordsTheOfferedVersionInSettings()
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        var previous = settings.SkippedUpdateVersion;
        try
        {
            var viewModel = CreateViewModel();
            viewModel.ApplyResult(new AppUpdateCheckResult(Release("7.7.7"), true,
                AppUpdateService.SelectPackage(Release("7.7.7",
                    new GitHubAsset(PackageNameForThisPlatform(), "https://example/pkg", 1, Sha))), null));

            viewModel.SkipVersion();

            Assert.Equal("7.7.7", SettingsPersistenceService.Instance.LoadSettings().SkippedUpdateVersion);
        }
        finally
        {
            var restore = SettingsPersistenceService.Instance.LoadSettings();
            restore.SkippedUpdateVersion = previous;
            _ = SettingsPersistenceService.Instance.TrySaveSettings(restore, out _);
        }
    }

    private static CheckUpdateViewModel CreateViewModel()
        => new(new AppUpdateService(new HttpClient()), new TestLocalizer());

    private static string PackageNameForThisPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "uotanstudio-win-x64.zip";
        }

        return OperatingSystem.IsMacOS()
            ? "uotanstudio-osx-arm64.dmg"
            : "uotanstudio-linux-x64.deb";
    }

    private sealed class TestLocalizer : UiAbstractions.ILocalizer
    {
        public string GetString(string key) => key;

        public string GetString(string key, params object[] args)
            => args.Length == 0 ? key : key + ":" + string.Join(",", args);
    }
}
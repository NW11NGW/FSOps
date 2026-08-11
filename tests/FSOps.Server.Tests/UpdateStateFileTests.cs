using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// The updater's state file. It holds the user's on/off preference, so the one outcome that would
/// actually matter to someone - a corrupt or unreadable file taking the app down, or silently
/// turning the check back on after they turned it off - is what these tests are about.
/// </summary>
public class UpdateStateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fsops-update-state-tests", Guid.NewGuid().ToString("N"));

    private string Path0 => Path.Combine(_directory, UpdateStateFile.FileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AMissingFile_ReadsAsDefaults_WithTheCheckSwitchedOn()
    {
        var state = UpdateStateFile.Read(Path0);

        Assert.True(state.Enabled);
        Assert.Null(state.LastCheckedUtc);
        Assert.Null(state.LatestVersion);
    }

    [Fact]
    public void EverythingWrittenIsReadBackUnchanged()
    {
        var written = new UpdateState
        {
            Enabled = false,
            LastCheckedUtc = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            LastCheckFailed = true,
            LatestVersion = "0.2.0",
            ReleaseUrl = "https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0",
            ReleaseNotes = "Notes.",
            ReleasePublishedUtc = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            InstallerAssetName = "FSOps-Setup-0.2.0.exe",
            InstallerDownloadUrl = "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0/FSOps-Setup-0.2.0.exe",
            ChecksumDownloadUrl = "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0/FSOps-Setup-0.2.0.exe.sha256",
            DismissedVersion = "0.2.0",
            VerifiedFilePath = @"C:\Users\someone\AppData\Local\FSOps\updates\FSOps-Setup-0.2.0.exe",
            VerifiedSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            VerifiedVersion = "0.2.0",
        };

        UpdateStateFile.Write(Path0, written);
        var read = UpdateStateFile.Read(Path0);

        Assert.False(read.Enabled);
        Assert.Equal(written.LastCheckedUtc, read.LastCheckedUtc);
        Assert.True(read.LastCheckFailed);
        Assert.Equal(written.LatestVersion, read.LatestVersion);
        Assert.Equal(written.ReleaseUrl, read.ReleaseUrl);
        Assert.Equal(written.ReleaseNotes, read.ReleaseNotes);
        Assert.Equal(written.ReleasePublishedUtc, read.ReleasePublishedUtc);
        Assert.Equal(written.InstallerAssetName, read.InstallerAssetName);
        Assert.Equal(written.InstallerDownloadUrl, read.InstallerDownloadUrl);
        Assert.Equal(written.ChecksumDownloadUrl, read.ChecksumDownloadUrl);
        Assert.Equal(written.DismissedVersion, read.DismissedVersion);
        Assert.Equal(written.VerifiedFilePath, read.VerifiedFilePath);
        Assert.Equal(written.VerifiedSha256, read.VerifiedSha256);
        Assert.Equal(written.VerifiedVersion, read.VerifiedVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "enabled": "yes please" }""")]
    public void ACorruptFile_DegradesToDefaultsRatherThanThrowing(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path0, contents);

        var state = UpdateStateFile.Read(Path0);

        Assert.True(state.Enabled);
        Assert.Null(state.LatestVersion);
    }

    [Fact]
    public void WritingCreatesTheDirectory_AndLeavesNoTemporaryFileBehind()
    {
        UpdateStateFile.Write(Path0, new UpdateState { LatestVersion = "0.4.0" });

        Assert.True(File.Exists(Path0));
        Assert.False(File.Exists(Path0 + ".tmp"));
        Assert.Equal("0.4.0", UpdateStateFile.Read(Path0).LatestVersion);
    }

    [Fact]
    public void SwitchingTheCheckOff_IsWhatSurvivesARestart()
    {
        UpdateStateFile.Write(Path0, new UpdateState { Enabled = false });

        Assert.False(UpdateStateFile.Read(Path0).Enabled);
    }
}

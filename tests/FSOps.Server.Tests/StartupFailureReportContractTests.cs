using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// The server's half of the contract it shares with the desktop shell: exit with
/// <see cref="StartupFailureReport.ExitCode"/> and leave an explanation in
/// <see cref="StartupFailureReport.FileName"/>, and the shell will show that text verbatim instead
/// of "It exited with code N".
///
/// <para>The shell cannot reference this assembly - it is a window and a process supervisor, and
/// taking a dependency on the server to read two constants would be the wrong shape - so it repeats
/// the two values. Only the contract is duplicated; the message itself never is, because that is
/// the part that would actually rot. Both sides pin their half:
/// <c>StartupErrorReportTests.TheShellAndServerMustAgreeOnTheContract</c> in FSOps.Desktop.Tests is
/// the other one. Change either value and the opposite test fails.</para>
/// </summary>
public class StartupFailureReportContractTests : IDisposable
{
    private readonly string? _originalContents;
    private readonly bool _existedBefore;

    public StartupFailureReportContractTests()
    {
        // AppPaths caches the data directory for the life of the process, so this cannot be
        // redirected here - FSOPS_DATA_DIR is already set for the test run. Preserve and restore
        // whatever was there instead of assuming the file is ours to clobber.
        _existedBefore = File.Exists(StartupFailureReport.FilePath);
        _originalContents = _existedBefore ? File.ReadAllText(StartupFailureReport.FilePath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_existedBefore && _originalContents is not null)
            {
                File.WriteAllText(StartupFailureReport.FilePath, _originalContents);
            }
            else if (File.Exists(StartupFailureReport.FilePath))
            {
                File.Delete(StartupFailureReport.FilePath);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheShellAndServerMustAgreeOnTheContract()
    {
        Assert.Equal(3, StartupFailureReport.ExitCode);
        Assert.Equal("startup-error.txt", StartupFailureReport.FileName);
    }

    [Fact]
    public void WritingAReportStoresTheMessageAndReturnsTheExitCode()
    {
        const string message = "FSOps cannot open its database.\n\n  somewhere\\fsops.db";

        var code = StartupFailureReport.Write(message);

        Assert.Equal(StartupFailureReport.ExitCode, code);
        Assert.Equal(message.ReplaceLineEndings(), File.ReadAllText(StartupFailureReport.FilePath));
    }

    [Fact]
    public void ClearingRemovesAStaleReportSoASuccessfulLaunchNeverShowsOne()
    {
        StartupFailureReport.Write("something went wrong last time");
        Assert.True(File.Exists(StartupFailureReport.FilePath));

        StartupFailureReport.Clear();

        Assert.False(File.Exists(StartupFailureReport.FilePath));

        // And clearing when there is nothing to clear is not an error.
        StartupFailureReport.Clear();
    }
}

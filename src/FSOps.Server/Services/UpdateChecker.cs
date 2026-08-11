using System.Diagnostics;
using System.Security.Cryptography;
using FSOps.Core.Time;

namespace FSOps.Server.Services;

/// <summary>What the app is doing about a downloaded installer right now.</summary>
public static class UpdateDownloadStates
{
    public const string None = "none";
    public const string Downloading = "downloading";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>
/// The whole updater state as the SPA sees it. Note what is absent: there is no field that can
/// carry the path of an unverified file, because a file that has not matched its published checksum
/// is deleted before this record is ever built.
/// </summary>
public sealed record UpdateStatusResponse(
    bool Enabled,
    bool Checking,
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool Dismissed,
    DateTimeOffset? LastCheckedUtc,
    bool LastCheckFailed,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset? ReleasePublishedUtc,
    bool DownloadAvailable,
    string DownloadState,
    string? DownloadFileName,
    string? DownloadSha256,
    long DownloadedBytes,
    string? DownloadMessage);

/// <summary>Outcome of asking the app to show a verified installer in Explorer.</summary>
public sealed record RevealResult(bool Success, string? Message);

/// <summary>
/// Decides whether a newer FSOps release exists, and - only when the user asks - downloads and
/// verifies its installer.
///
/// <para><b>How far this goes on its own, and why.</b> It checks, and it tells you. It will not
/// download anything until you click, and it will never, under any circumstance, execute an
/// installer. FSOps ships unsigned, so the SHA-256 published alongside the release is the only thing
/// standing between "a file from the internet" and "the file the author built". An updater that
/// silently ran a downloaded binary would be constructing precisely the attack path that checksum
/// exists to close, and would do it with the user's own trust in the app. The final step - deciding
/// to run an installer - stays with the person, in Explorer, where they can see what they are
/// launching. The app only ever opens the containing folder; the installer's own path is never
/// handed to ShellExecute, so there is no code path here that can start it even by mistake.</para>
///
/// <para><b>How it fails.</b> Silently, always. No internet, DNS down, GitHub down, rate limited,
/// corporate proxy, an offline sim rig - every one of those produces exactly what "you are up to
/// date" produces, and is written only to the log. Nothing is put in front of the user. The check is
/// also entirely off the startup path: there is no hosted service and no timer. The first request
/// for the status endpoint kicks off a background refresh at most once a day and returns whatever
/// was already cached, so even a hanging network call cannot delay a page or the app starting.</para>
///
/// <para><b>What it refuses.</b> Drafts and pre-releases are never offered. A tag that is not a
/// parseable version is never offered. A release that is not strictly newer than the running build,
/// by real semantic comparison, is never offered. A downloaded file whose SHA-256 does not match the
/// release's published checksum is deleted, never moved into place, and never named to the user. And
/// if a release ships an installer but no checksum, the user is told a new version exists and given
/// the release page - but the in-app download is not offered at all, because there would be nothing
/// to verify it against.</para>
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>How long a successful check is trusted before another is made. Deliberately long -
    /// the check is meant to be cheap and rare, and nothing here is time-critical.</summary>
    public static readonly TimeSpan SuccessfulCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Retry interval after a failed check. Shorter than the success interval so a machine
    /// that was briefly offline recovers the same day, long enough that a permanently offline
    /// machine costs at most a handful of failed DNS lookups a day.</summary>
    public static readonly TimeSpan FailedCheckInterval = TimeSpan.FromHours(6);

    /// <summary>Refuses absurd installer sizes rather than filling the user's disk.</summary>
    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    private const int MaxChecksumBytes = 4096;

    private const string InstallerPreferredPrefix = "FSOps-Setup";
    private const string InstallerExtension = ".exe";
    private const string ChecksumExtension = ".sha256";

    private readonly IGitHubReleaseClient _client;
    private readonly IUpdateStorage _store;
    private readonly IClock _clock;
    private readonly ILogger<UpdateChecker> _logger;

    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    private volatile bool _checking;
    private volatile string _downloadState = UpdateDownloadStates.None;
    private volatile string? _downloadMessage;
    private long _downloadedBytes;

    public UpdateChecker(
        IGitHubReleaseClient client,
        IUpdateStorage store,
        IClock clock,
        ILogger<UpdateChecker> logger)
    {
        _client = client;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>%LOCALAPPDATA%\FSOps\updates - never beside the executable, which is read-only for a
    /// standard user once the app is installed into Program Files.</summary>
    public string UpdatesDirectory => _store.UpdatesDirectory;

    /// <summary>
    /// The running build's version - single source of truth, see <see cref="AppVersion"/>. Settable
    /// at construction only, so tests can pin a version and assert the newer/equal/older rules
    /// without their result depending on how the test host itself happens to be versioned. Nothing
    /// in the app ever sets it; dependency injection leaves the default in place.
    /// </summary>
    public string CurrentVersion { get; init; } = AppVersion.Current;

    /// <summary>
    /// Returns the cached status without waiting on anything. This is what the status endpoint
    /// serves, which is why it does no I/O beyond a small local file read: the SPA asking about
    /// updates must never be able to sit behind a network call.
    /// </summary>
    public UpdateStatusResponse GetStatus() => BuildStatus(_store.Load());

    /// <summary>
    /// Starts a check in the background if one is due, and returns immediately. Does nothing at all -
    /// including opening no connection - when the feature is switched off or a check is already
    /// running or the cached result is still fresh.
    /// </summary>
    public void BeginBackgroundCheck()
    {
        var state = _store.Load();
        if (!state.Enabled || _checking || !IsCheckDue(state))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(force: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A background update check must never be able to take the process down, whatever
                // it manages to throw.
                _logger.LogDebug(ex, "Background update check failed");
            }
        });
    }

    /// <summary>
    /// Performs a check, awaited. Used by the explicit "check now" action and by tests, which need a
    /// deterministic point to assert on rather than a fire-and-forget task.
    /// </summary>
    public async Task<UpdateStatusResponse> CheckAsync(bool force, CancellationToken ct)
    {
        var state = _store.Load();

        // The kill switch, checked before the HTTP client is touched. "Off" means no request.
        if (!state.Enabled)
        {
            return BuildStatus(state);
        }

        if (!force && !IsCheckDue(state))
        {
            return BuildStatus(state);
        }

        await _checkGate.WaitAsync(ct);
        _checking = true;
        try
        {
            state = await RunCheckAsync(force, ct);
        }
        finally
        {
            _checking = false;
            _checkGate.Release();
        }

        // Built AFTER _checking is cleared, deliberately. Building it inside the try would have this
        // method's own completed result claim a check was still running, which sends the client into
        // a poll loop waiting for something that has already finished. Found by watching the live
        // response, not by a unit test - hence the regression test that now pins it.
        return BuildStatus(state);
    }

    private async Task<UpdateState> RunCheckAsync(bool force, CancellationToken ct)
    {
        // Re-read after taking the gate: another caller may have just finished a check while this
        // one was queued, and repeating it would be pure waste.
        var state = _store.Load();
        if (!state.Enabled || (!force && !IsCheckDue(state)))
        {
            return state;
        }

        var lookup = await _client.GetLatestReleaseAsync(ct);
        state.LastCheckedUtc = _clock.UtcNow;

        if (!lookup.Success || lookup.Release is null)
        {
            // There is nothing the user could do about this and nothing they need to know. Log it,
            // and leave whatever we already knew in place.
            state.LastCheckFailed = true;
            _logger.LogInformation("Update check did not complete ({Reason}) - treating as no update available", lookup.FailureReason);
            _store.Save(state);
            return state;
        }

        state.LastCheckFailed = false;
        ApplyRelease(state, lookup.Release);
        _store.Save(state);
        return state;
    }

    /// <summary>Turns the whole feature on or off. Switching it off also forgets any verified
    /// download, so nothing is left lying in the data directory that the user did not ask for.</summary>
    public UpdateStatusResponse SetEnabled(bool enabled)
    {
        var state = _store.Load();
        state.Enabled = enabled;

        if (!enabled)
        {
            ClearVerifiedDownload(state);
            _downloadState = UpdateDownloadStates.None;
            _downloadMessage = null;
            Interlocked.Exchange(ref _downloadedBytes, 0);
        }

        _store.Save(state);
        return BuildStatus(state);
    }

    /// <summary>Records that the user does not want to be told about this particular version again.
    /// A later release clears it automatically.</summary>
    public UpdateStatusResponse Dismiss()
    {
        var state = _store.Load();
        state.DismissedVersion = state.LatestVersion;
        _store.Save(state);
        return BuildStatus(state);
    }

    /// <summary>
    /// Starts a download in the background and returns the current status. The download is only ever
    /// reached from an explicit user action - nothing here runs on its own.
    /// </summary>
    public UpdateStatusResponse BeginDownload()
    {
        var state = _store.Load();

        // The user asked for something. Answering with silence would leave the UI on a spinner
        // forever, so a request that cannot be honoured says so - it just never says so by
        // producing a file. The most common reason to land here is a release that ships an
        // installer with no checksum: nothing to verify against means nothing gets fetched.
        if (!CanDownload(state))
        {
            return FailDownload(state, "There is nothing available to download.");
        }

        if (_downloadState == UpdateDownloadStates.Downloading)
        {
            return BuildStatus(state);
        }

        _downloadState = UpdateDownloadStates.Downloading;
        _downloadMessage = null;
        Interlocked.Exchange(ref _downloadedBytes, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update download failed unexpectedly");
                _downloadState = UpdateDownloadStates.Failed;
                _downloadMessage = "The download could not be completed.";
            }
        });

        return BuildStatus(state);
    }

    /// <summary>
    /// Downloads the installer and verifies it, awaited. The order here is the security-relevant
    /// part and is deliberate:
    /// <list type="number">
    /// <item>fetch the published checksum FIRST - if there isn't one, stop before downloading
    /// anything, because an installer we cannot verify is worse than no installer;</item>
    /// <item>stream the installer to a temporary <c>.part</c> path, never to its final name, so an
    /// interrupted download can never be mistaken for a verified one;</item>
    /// <item>hash the bytes that actually landed <b>on disk</b>, re-read from the file - not a hash
    /// computed over the stream on its way past, which would verify what we received rather than
    /// what we kept;</item>
    /// <item>only on an exact match, move it into place and record it.</item>
    /// </list>
    /// On any mismatch the file is deleted and nothing is recorded. There is no branch that leaves
    /// an unverified file in the updates directory or names one to the user.
    /// </summary>
    public async Task<UpdateStatusResponse> DownloadAsync(CancellationToken ct)
    {
        var state = _store.Load();
        if (!CanDownload(state))
        {
            _downloadState = UpdateDownloadStates.Failed;
            _downloadMessage = "There is nothing available to download.";
            return BuildStatus(state);
        }

        await _downloadGate.WaitAsync(ct);
        try
        {
            state = _store.Load();
            if (!CanDownload(state))
            {
                _downloadState = UpdateDownloadStates.Failed;
                _downloadMessage = "There is nothing available to download.";
                return BuildStatus(state);
            }

            _downloadState = UpdateDownloadStates.Downloading;
            _downloadMessage = null;
            Interlocked.Exchange(ref _downloadedBytes, 0);

            var fileName = SafeAssetFileName(state.InstallerAssetName);
            if (fileName is null ||
                !Uri.TryCreate(state.InstallerDownloadUrl, UriKind.Absolute, out var installerUrl) ||
                !Uri.TryCreate(state.ChecksumDownloadUrl, UriKind.Absolute, out var checksumUrl))
            {
                return FailDownload(state, "The release does not describe an installer this app can verify.");
            }

            // 1. The checksum first. No checksum, no download - there would be nothing to check the
            //    bytes against, and an unverified unsigned installer is the exact hole this closes.
            var checksumText = await _client.DownloadTextAsync(checksumUrl, MaxChecksumBytes, ct);
            var expectedHash = ParseSha256(checksumText.Text);
            if (!checksumText.Success || expectedHash is null)
            {
                _logger.LogWarning("Update download stopped: checksum unavailable or unreadable ({Reason})", checksumText.FailureReason);
                return FailDownload(state, "The release's checksum could not be read, so the download was not attempted.");
            }

            Directory.CreateDirectory(UpdatesDirectory);
            var finalPath = Path.Combine(UpdatesDirectory, fileName);
            var partialPath = finalPath + ".part";
            TryDelete(partialPath);

            // 2. Stream to a temporary name.
            var download = await _client.DownloadToFileAsync(installerUrl, partialPath, MaxInstallerBytes, ct);
            if (!download.Success)
            {
                TryDelete(partialPath);
                _logger.LogWarning("Update download failed ({Reason})", download.FailureReason);
                return FailDownload(state, "The installer could not be downloaded.");
            }

            Interlocked.Exchange(ref _downloadedBytes, download.BytesWritten);

            // 3. Hash what is on disk, not what went past.
            var actualHash = await ComputeSha256Async(partialPath, ct);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                _logger.LogError(
                    "Update download DISCARDED - SHA-256 mismatch. Expected {Expected}, got {Actual}",
                    expectedHash,
                    actualHash);
                return FailDownload(state, "The downloaded file did not match the release's checksum, so it was deleted. Get the installer from the release page instead.");
            }

            // 4. Verified. Only now does it get its real name.
            TryDelete(finalPath);
            File.Move(partialPath, finalPath, overwrite: true);

            state.VerifiedFilePath = finalPath;
            state.VerifiedSha256 = actualHash;
            state.VerifiedVersion = state.LatestVersion;
            _store.Save(state);

            _downloadState = UpdateDownloadStates.Ready;
            _downloadMessage = null;
            _logger.LogInformation("Update {Version} downloaded and SHA-256 verified", state.LatestVersion);
            return BuildStatus(state);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>
    /// Opens the updates folder in Explorer, after re-hashing the file to prove it is still the one
    /// that was verified. Re-checking here rather than trusting the record from download time closes
    /// the gap in between - the file sits on disk for as long as the user takes to click, and
    /// "verified an hour ago" is not the same statement as "these bytes are correct now".
    /// <para>
    /// The folder is opened, never the file. The installer's path is deliberately never passed to
    /// the shell, so there is no code path in FSOps that can launch it.
    /// </para>
    /// </summary>
    public async Task<RevealResult> RevealAsync(CancellationToken ct)
    {
        var state = _store.Load();
        var path = state.VerifiedFilePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearVerifiedDownload(state);
            _store.Save(state);
            _downloadState = UpdateDownloadStates.None;
            return new RevealResult(false, "The downloaded installer is no longer there. Download it again.");
        }

        var actualHash = await ComputeSha256Async(path, ct);
        if (!string.Equals(actualHash, state.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Verified installer no longer matches its recorded SHA-256 - deleting it");
            TryDelete(path);
            ClearVerifiedDownload(state);
            _store.Save(state);
            _downloadState = UpdateDownloadStates.Failed;
            _downloadMessage = "The downloaded file changed since it was verified, so it was deleted.";
            return new RevealResult(false, "The downloaded file changed since it was verified, so it was deleted.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(UpdatesDirectory) { UseShellExecute = true });
            return new RevealResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the updates folder");
            return new RevealResult(false, $"Open this folder manually: {UpdatesDirectory}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Decision rules
    // ---------------------------------------------------------------------------------------

    private bool IsCheckDue(UpdateState state)
    {
        if (state.LastCheckedUtc is null)
        {
            return true;
        }

        var interval = state.LastCheckFailed ? FailedCheckInterval : SuccessfulCheckInterval;
        var elapsed = _clock.UtcNow - state.LastCheckedUtc.Value;

        // A clock that has jumped backwards (a timezone fix, a VM restore) would otherwise leave the
        // check permanently "not due"; treat any negative elapsed time as due.
        return elapsed < TimeSpan.Zero || elapsed >= interval;
    }

    /// <summary>
    /// Folds a fetched release into the stored state, applying every refusal rule. Kept separate and
    /// internal so each rule - draft, pre-release, unparseable tag, older, equal - can be asserted
    /// individually rather than only through a full HTTP round trip.
    /// </summary>
    internal void ApplyRelease(UpdateState state, GitHubRelease release)
    {
        if (release.IsDraft)
        {
            _logger.LogInformation("Latest release {Tag} is a draft - not offering it", release.TagName);
            ClearAvailableUpdate(state);
            return;
        }

        if (release.IsPrerelease)
        {
            _logger.LogInformation("Latest release {Tag} is a pre-release - not offering it", release.TagName);
            ClearAvailableUpdate(state);
            return;
        }

        if (!SemanticVersion.TryParse(release.TagName, out var released))
        {
            _logger.LogInformation("Latest release tag {Tag} is not a version this app can compare - not offering it", release.TagName);
            ClearAvailableUpdate(state);
            return;
        }

        // A tag can carry a pre-release suffix even when GitHub's own "prerelease" flag was not
        // ticked. Both are refusals.
        if (released.IsPrerelease)
        {
            _logger.LogInformation("Latest release tag {Tag} is a pre-release version - not offering it", release.TagName);
            ClearAvailableUpdate(state);
            return;
        }

        if (!SemanticVersion.TryParse(CurrentVersion, out var current))
        {
            _logger.LogWarning("The running version {Version} is not parseable - not offering any update", CurrentVersion);
            ClearAvailableUpdate(state);
            return;
        }

        // Strictly newer. Equal is not an update, and older is not either - a rollback is never
        // something this offers.
        if (released <= current)
        {
            ClearAvailableUpdate(state);
            return;
        }

        var installer = SelectInstallerAsset(release.Assets);
        var checksum = installer is null ? null : SelectChecksumAsset(release.Assets, installer);

        state.LatestVersion = released.ToString();
        state.ReleaseUrl = release.HtmlUrl;
        state.ReleaseNotes = Truncate(release.Body, 4000);
        state.ReleasePublishedUtc = release.PublishedAtUtc;
        state.InstallerAssetName = installer?.Name;
        state.InstallerDownloadUrl = installer?.DownloadUrl.ToString();
        state.ChecksumDownloadUrl = checksum?.DownloadUrl.ToString();

        if (installer is null)
        {
            _logger.LogInformation("Release {Tag} has no installer asset - linking to the release page only", release.TagName);
        }
        else if (checksum is null)
        {
            _logger.LogWarning(
                "Release {Tag} ships {Installer} with no {Extension} checksum - the in-app download is disabled for it",
                release.TagName,
                installer.Name,
                ChecksumExtension);
        }

        // A dismissal only silences the version it was made about.
        if (!string.Equals(state.DismissedVersion, state.LatestVersion, StringComparison.OrdinalIgnoreCase))
        {
            state.DismissedVersion = null;
        }

        // A file verified for an older release is no longer the update being offered.
        if (!string.Equals(state.VerifiedVersion, state.LatestVersion, StringComparison.OrdinalIgnoreCase))
        {
            ClearVerifiedDownload(state);
        }
    }

    /// <summary>
    /// The installer asset for a release: an <c>.exe</c>, preferring the conventional
    /// <c>FSOps-Setup-x.y.z.exe</c> name. Anything whose filename does not survive sanitising is
    /// ignored outright rather than repaired - a release asset called something strange is a reason
    /// to do nothing, not a puzzle to solve.
    /// </summary>
    internal static ReleaseAsset? SelectInstallerAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        var candidates = assets
            .Where(a => SafeAssetFileName(a.Name) is not null)
            .Where(a => a.Name.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase))
            .Where(a => GitHubReleaseClient.IsAllowedDownloadUrl(a.DownloadUrl))
            .ToList();

        return candidates.FirstOrDefault(a => a.Name.StartsWith(InstallerPreferredPrefix, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>The checksum sidecar for a given installer: exactly <c>&lt;installer&gt;.sha256</c>.
    /// A loosely-matched checksum file would defeat the point.</summary>
    internal static ReleaseAsset? SelectChecksumAsset(IReadOnlyList<ReleaseAsset> assets, ReleaseAsset installer)
    {
        var expected = installer.Name + ChecksumExtension;
        return assets.FirstOrDefault(a =>
            string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase) &&
            GitHubReleaseClient.IsAllowedDownloadUrl(a.DownloadUrl));
    }

    /// <summary>
    /// Pulls a SHA-256 out of a checksum file. Accepts the <c>sha256sum</c> layout
    /// (<c>&lt;hex&gt;  filename</c>), a bare hash on its own, and PowerShell's upper-case
    /// <c>Get-FileHash</c> output, so whichever tool generates the release artefact will work.
    /// Returns null for anything that does not contain exactly one recognisable 64-character hash.
    /// </summary>
    internal static string? ParseSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separators = new[] { ' ', '\t', '\r', '\n', '*', ':', '=', ',' };
        foreach (var token in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
            {
                return token.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// A release asset name is only usable as a filename if it is plain: letters, digits, dot,
    /// dash, underscore, and nothing else. Path separators, drive letters, leading dots and
    /// anything over-long are rejected rather than escaped, because the app has no need to accept
    /// an exotic filename and every reason not to build a path out of a string it got over the wire.
    /// </summary>
    internal static string? SafeAssetFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 128 || trimmed[0] == '.' || trimmed[^1] == '.')
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return null;
            }
        }

        return trimmed;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private bool CanDownload(UpdateState state) =>
        state.Enabled &&
        !string.IsNullOrWhiteSpace(state.LatestVersion) &&
        !string.IsNullOrWhiteSpace(state.InstallerDownloadUrl) &&
        !string.IsNullOrWhiteSpace(state.ChecksumDownloadUrl) &&
        SafeAssetFileName(state.InstallerAssetName) is not null;

    private UpdateStatusResponse FailDownload(UpdateState state, string message)
    {
        _downloadState = UpdateDownloadStates.Failed;
        _downloadMessage = message;
        return BuildStatus(state);
    }

    private static void ClearAvailableUpdate(UpdateState state)
    {
        state.LatestVersion = null;
        state.ReleaseUrl = null;
        state.ReleaseNotes = null;
        state.ReleasePublishedUtc = null;
        state.InstallerAssetName = null;
        state.InstallerDownloadUrl = null;
        state.ChecksumDownloadUrl = null;
        state.DismissedVersion = null;
        ClearVerifiedDownload(state);
    }

    private static void ClearVerifiedDownload(UpdateState state)
    {
        if (!string.IsNullOrWhiteSpace(state.VerifiedFilePath))
        {
            TryDelete(state.VerifiedFilePath);
        }

        state.VerifiedFilePath = null;
        state.VerifiedSha256 = null;
        state.VerifiedVersion = null;
    }

    private UpdateStatusResponse BuildStatus(UpdateState state)
    {
        var updateAvailable = !string.IsNullOrWhiteSpace(state.LatestVersion);
        var dismissed = updateAvailable &&
            string.Equals(state.DismissedVersion, state.LatestVersion, StringComparison.OrdinalIgnoreCase);

        var verified = updateAvailable &&
            !string.IsNullOrWhiteSpace(state.VerifiedFilePath) &&
            string.Equals(state.VerifiedVersion, state.LatestVersion, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(state.VerifiedFilePath);

        // A failed attempt wins over an already-verified file from an earlier one. A checksum
        // mismatch is the single most important thing this feature can ever have to say, and
        // reporting "ready" because a previous download happened to be fine would swallow exactly
        // that. The verified file is still named below, so nothing is lost by saying so plainly.
        var downloadState = _downloadState switch
        {
            UpdateDownloadStates.Failed => UpdateDownloadStates.Failed,
            UpdateDownloadStates.Downloading => UpdateDownloadStates.Downloading,
            _ => verified ? UpdateDownloadStates.Ready : UpdateDownloadStates.None,
        };

        return new UpdateStatusResponse(
            Enabled: state.Enabled,
            Checking: _checking,
            CurrentVersion: CurrentVersion,
            LatestVersion: state.LatestVersion,
            UpdateAvailable: updateAvailable,
            Dismissed: dismissed,
            LastCheckedUtc: state.LastCheckedUtc,
            LastCheckFailed: state.LastCheckFailed,
            ReleaseUrl: state.ReleaseUrl,
            ReleaseNotes: state.ReleaseNotes,
            ReleasePublishedUtc: state.ReleasePublishedUtc,
            DownloadAvailable: CanDownload(state),
            DownloadState: downloadState,
            DownloadFileName: verified ? Path.GetFileName(state.VerifiedFilePath) : null,
            DownloadSha256: verified ? state.VerifiedSha256 : null,
            DownloadedBytes: Interlocked.Read(ref _downloadedBytes),
            DownloadMessage: _downloadMessage);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do - the file is not offered to the user either way.
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null :
        value.Length <= maxLength ? value : value[..maxLength];
}

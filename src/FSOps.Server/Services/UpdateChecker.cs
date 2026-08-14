using System.Diagnostics;
using System.Security.Cryptography;
using FSOps.Core.Entities;
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
    string? DownloadMessage,

    /// <summary>"stable" or "development" - see <see cref="UpdateChannel"/>.</summary>
    string Channel,

    /// <summary>The running build is newer than anything this channel has. See
    /// <see cref="UpdateState.AheadOfChannel"/>; the UI has to say so rather than claim the user is
    /// up to date, because they are not - they are past it.</summary>
    bool AheadOfChannel,

    /// <summary>The newest version the channel holds, offered or not. Null when the last check never
    /// produced a comparable release.</summary>
    string? ChannelNewestVersion);

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
/// <para><b>What it refuses.</b> Drafts are never offered, on either channel. Pre-releases are never
/// offered on the stable channel. A tag that is not a parseable version is never offered. A release
/// that is not strictly newer than the running build, by real semantic comparison, is never offered.
/// A downloaded file whose SHA-256 does not match the release's published checksum is deleted, never
/// moved into place, and never named to the user. And if a release ships an installer but no
/// checksum, the user is told a new version exists and given the release page - but the in-app
/// download is not offered at all, because there would be nothing to verify it against.</para>
///
/// <para><b>Channels.</b> <see cref="UpdateChannel.Stable"/> reads <c>/releases/latest</c>, which
/// GitHub documents as excluding drafts and pre-releases, so nothing unreleased can reach it.
/// <see cref="UpdateChannel.Development"/> lists recent releases and takes the highest version among
/// them, pre-releases included - which is how a development build reaches somebody who opted in, and
/// the pre-release flag is what stops it reaching anybody who did not.</para>
///
/// <para><b>What the channel does NOT change.</b> Verification. A pre-release's installer is fetched
/// and checked against its published <c>.sha256</c> by exactly the same code, in exactly the same
/// order, as a stable one - <see cref="DownloadAsync"/> never learns which channel produced the URLs
/// it was handed, and there is deliberately no parameter by which it could. The channel decides
/// <i>which</i> build is offered; it has no say in <i>whether</i> it is checked.</para>
///
/// <para><b>Being ahead of the channel.</b> Someone running a development build who switches back to
/// stable is running something newer than the newest stable release. There is no update for them and
/// there is no downgrade either - <see cref="ApplyRelease"/> offers strictly-newer releases only, so
/// an older build can never be presented as an upgrade. That state is reported as its own fact
/// (<see cref="UpdateState.AheadOfChannel"/>) rather than dressed up as "you are up to date", because
/// they are not up to date, they are past it, and they need to know nothing will be offered until
/// stable catches up.</para>
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

    /// <summary>
    /// How many releases the development channel looks at. Generous enough that a run of betas
    /// followed by the stable release they led to are all still in view, small enough to stay one
    /// cheap request. The newest is picked by version, not by position, so this only ever bounds how
    /// far back the check can see - never which release wins.
    /// </summary>
    private const int DevelopmentChannelReleaseCount = 20;

    private readonly IGitHubReleaseClient _client;
    private readonly IUpdateStorage _store;
    private readonly IUpdateChannelStore _channels;
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
        IUpdateChannelStore channels,
        IClock clock,
        ILogger<UpdateChecker> logger)
    {
        _client = client;
        _store = store;
        _channels = channels;
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
    public async Task<UpdateStatusResponse> GetStatusAsync(CancellationToken ct) =>
        BuildStatus(_store.Load(), await _channels.GetAsync(ct));

    /// <summary>
    /// Starts a check in the background if one is due, and returns immediately. Does nothing at all -
    /// including opening no connection - when the feature is switched off or a check is already
    /// running or the cached result is still fresh.
    /// </summary>
    public void BeginBackgroundCheck()
    {
        // The kill switch is read synchronously here so that "off" costs nothing at all, not even a
        // queued task. Whether the check is actually due depends on the channel, which needs a read,
        // so that decision happens inside the task - CheckAsync makes it again anyway.
        if (!_store.Load().Enabled || _checking)
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
        var channel = await _channels.GetAsync(ct);

        // The kill switch, checked before the HTTP client is touched. "Off" means no request.
        if (!state.Enabled)
        {
            return BuildStatus(state, channel);
        }

        if (!force && !IsCheckDue(state, channel))
        {
            return BuildStatus(state, channel);
        }

        await _checkGate.WaitAsync(ct);
        _checking = true;
        try
        {
            state = await RunCheckAsync(force, channel, ct);
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
        return BuildStatus(state, channel);
    }

    private async Task<UpdateState> RunCheckAsync(bool force, UpdateChannel channel, CancellationToken ct)
    {
        // Re-read after taking the gate: another caller may have just finished a check while this
        // one was queued, and repeating it would be pure waste.
        var state = _store.Load();
        if (!state.Enabled || (!force && !IsCheckDue(state, channel)))
        {
            return state;
        }

        var (success, release, failureReason) = await FetchForChannelAsync(channel, ct);
        state.LastCheckedUtc = _clock.UtcNow;

        // Recorded whether or not the lookup succeeded, and that ordering is load-bearing rather than
        // tidy. CheckedChannel means "the channel this attempt was made against", not "the channel
        // that answered". Leaving it unset on failure would make IsCheckDue see a channel mismatch on
        // every subsequent call, which defeats the failed-check backoff completely and turns a machine
        // that is simply offline into a retry storm against GitHub.
        state.CheckedChannel = channel.ToString();

        if (!success)
        {
            // There is nothing the user could do about this and nothing they need to know. Log it,
            // and leave whatever we already knew in place.
            state.LastCheckFailed = true;
            _logger.LogInformation("Update check did not complete ({Reason}) - treating as no update available", failureReason);
            _store.Save(state);
            return state;
        }

        state.LastCheckFailed = false;
        ApplyRelease(state, release, channel);
        _store.Save(state);
        return state;
    }

    /// <summary>
    /// Asks the channel's feed for the release it should be judged against.
    /// <para>
    /// Stable reads <c>/releases/latest</c>, which cannot return a pre-release however it is tagged -
    /// so the stable channel's central guarantee is enforced by GitHub before this code sees
    /// anything, and the refusal rules in <see cref="ApplyRelease"/> are a second, independent line
    /// rather than the only one.
    /// </para>
    /// <para>
    /// Development lists releases and picks the highest version among them, which is deliberately
    /// <b>not</b> "the newest pre-release": if a stable release outranks every beta, a development
    /// user gets the stable release. Opting in to development means seeing more, never being pinned
    /// to something older.
    /// </para>
    /// <para>
    /// A successful lookup that yields no usable release is a success with a null release - the
    /// difference matters, because a failure keeps whatever was already known while a success with
    /// nothing in it correctly clears a stale offer.
    /// </para>
    /// </summary>
    private async Task<(bool Success, GitHubRelease? Release, string? FailureReason)> FetchForChannelAsync(
        UpdateChannel channel,
        CancellationToken ct)
    {
        if (channel == UpdateChannel.Development)
        {
            var list = await _client.GetRecentReleasesAsync(DevelopmentChannelReleaseCount, ct);
            return list.Success
                ? (true, SelectDevelopmentRelease(list.Releases), null)
                : (false, null, list.FailureReason);
        }

        var lookup = await _client.GetLatestReleaseAsync(ct);
        return lookup.Success
            ? (true, lookup.Release, null)
            : (false, null, lookup.FailureReason);
    }

    /// <summary>
    /// The highest version among a list of releases, pre-releases included, drafts excluded.
    /// Compared as semantic versions rather than taken in the order GitHub returned them: releases
    /// come back newest-published first, and publication order is not version order - a patch to an
    /// older line published after a beta would otherwise win.
    /// </summary>
    internal static GitHubRelease? SelectDevelopmentRelease(IReadOnlyList<GitHubRelease> releases)
    {
        GitHubRelease? best = null;
        SemanticVersion? bestVersion = null;

        foreach (var release in releases)
        {
            // A draft is refused on every channel. It is unpublished work, and on top of that GitHub
            // does not disclose drafts to an unauthenticated caller at all, so this is belt and
            // braces rather than the only guard.
            if (release.IsDraft || !SemanticVersion.TryParse(release.TagName, out var version))
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>Turns the whole feature on or off. Switching it off also forgets any verified
    /// download, so nothing is left lying in the data directory that the user did not ask for.</summary>
    public async Task<UpdateStatusResponse> SetEnabledAsync(bool enabled, CancellationToken ct)
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
        return BuildStatus(state, await _channels.GetAsync(ct));
    }

    /// <summary>
    /// Switches which releases the updater may offer, and immediately re-checks against the new one.
    ///
    /// <para>The cached result is thrown away first, and that is the point of this method rather than
    /// a detail of it. Everything in the state file was an answer to "what does the other channel
    /// have"; keeping any of it would leave somebody who just switched to stable still being offered
    /// the pre-release they switched away from. Any installer already downloaded for that offer goes
    /// with it - a verified file for a release this channel does not have is not a download the user
    /// still wants, and leaving it on disk would let the "ready" state outlive the reason for it.</para>
    ///
    /// <para>Nothing about verification changes here. The channel selects which release's URLs are
    /// written into the state; the download path reads those URLs and checks the bytes against the
    /// published checksum with no knowledge of where they came from.</para>
    /// </summary>
    public async Task<UpdateStatusResponse> SetChannelAsync(UpdateChannel channel, CancellationToken ct)
    {
        await _channels.SetAsync(channel, ct);

        var state = _store.Load();
        ClearAvailableUpdate(state);
        state.CheckedChannel = null;

        // Not merely "due" - forgotten. A switch is an explicit question, and the honest answer to it
        // is not a cached one from the channel the user just left.
        state.LastCheckedUtc = null;
        state.LastCheckFailed = false;
        _store.Save(state);

        _downloadState = UpdateDownloadStates.None;
        _downloadMessage = null;
        Interlocked.Exchange(ref _downloadedBytes, 0);

        _logger.LogInformation("Update channel set to {Channel}", channel);

        // Re-checks straight away so the answer the user sees is about the channel they just chose.
        // Returns the cached (now empty) status untouched when checks are switched off, which is
        // correct: choosing a channel is not consent to start making requests.
        return await CheckAsync(force: true, ct);
    }

    /// <summary>Records that the user does not want to be told about this particular version again.
    /// A later release clears it automatically.</summary>
    public async Task<UpdateStatusResponse> DismissAsync(CancellationToken ct)
    {
        var state = _store.Load();
        state.DismissedVersion = state.LatestVersion;
        _store.Save(state);
        return BuildStatus(state, await _channels.GetAsync(ct));
    }

    /// <summary>
    /// Starts a download in the background and returns the current status. The download is only ever
    /// reached from an explicit user action - nothing here runs on its own.
    /// </summary>
    public async Task<UpdateStatusResponse> BeginDownloadAsync(CancellationToken ct)
    {
        var state = _store.Load();
        var channel = await _channels.GetAsync(ct);

        // The user asked for something. Answering with silence would leave the UI on a spinner
        // forever, so a request that cannot be honoured says so - it just never says so by
        // producing a file. The most common reason to land here is a release that ships an
        // installer with no checksum: nothing to verify against means nothing gets fetched.
        if (!CanDownload(state))
        {
            return FailDownload(state, channel, "There is nothing available to download.");
        }

        if (_downloadState == UpdateDownloadStates.Downloading)
        {
            return BuildStatus(state, channel);
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

        return BuildStatus(state, channel);
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
    /// <para>
    /// <b>The channel gets no say in any of this.</b> It is read once, at the top, and used for one
    /// thing only: filling in a field of the response so the UI can render which channel is selected.
    /// Nothing below reads it, and nothing below should ever be made to - a stable installer and a
    /// pre-release installer are the same problem here, and the answer to both is the same checksum.
    /// If you find yourself wanting to pass it further down, the thing you are about to weaken is the
    /// only control standing between an unsigned executable and the user's machine.
    /// </para>
    /// </summary>
    public async Task<UpdateStatusResponse> DownloadAsync(CancellationToken ct)
    {
        var channel = await _channels.GetAsync(ct);
        var state = _store.Load();
        if (!CanDownload(state))
        {
            _downloadState = UpdateDownloadStates.Failed;
            _downloadMessage = "There is nothing available to download.";
            return BuildStatus(state, channel);
        }

        await _downloadGate.WaitAsync(ct);
        try
        {
            state = _store.Load();
            if (!CanDownload(state))
            {
                _downloadState = UpdateDownloadStates.Failed;
                _downloadMessage = "There is nothing available to download.";
                return BuildStatus(state, channel);
            }

            _downloadState = UpdateDownloadStates.Downloading;
            _downloadMessage = null;
            Interlocked.Exchange(ref _downloadedBytes, 0);

            var fileName = SafeAssetFileName(state.InstallerAssetName);
            if (fileName is null ||
                !Uri.TryCreate(state.InstallerDownloadUrl, UriKind.Absolute, out var installerUrl) ||
                !Uri.TryCreate(state.ChecksumDownloadUrl, UriKind.Absolute, out var checksumUrl))
            {
                return FailDownload(state, channel, "The release does not describe an installer this app can verify.");
            }

            // 1. The checksum first. No checksum, no download - there would be nothing to check the
            //    bytes against, and an unverified unsigned installer is the exact hole this closes.
            var checksumText = await _client.DownloadTextAsync(checksumUrl, MaxChecksumBytes, ct);
            var expectedHash = ParseSha256(checksumText.Text);
            if (!checksumText.Success || expectedHash is null)
            {
                _logger.LogWarning("Update download stopped: checksum unavailable or unreadable ({Reason})", checksumText.FailureReason);
                return FailDownload(state, channel, "The release's checksum could not be read, so the download was not attempted.");
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
                return FailDownload(state, channel, "The installer could not be downloaded.");
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
                return FailDownload(state, channel, "The downloaded file did not match the release's checksum, so it was deleted. Get the installer from the release page instead.");
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
            return BuildStatus(state, channel);
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

    private bool IsCheckDue(UpdateState state, UpdateChannel channel)
    {
        if (state.LastCheckedUtc is null)
        {
            return true;
        }

        // A result cached from a different channel is not a stale answer to this question, it is an
        // answer to a different one. SetChannelAsync already clears the cache, so reaching this is
        // either a state file written before channels existed or one edited underneath us; either
        // way, re-asking is the only correct response.
        if (!string.Equals(state.CheckedChannel, channel.ToString(), StringComparison.OrdinalIgnoreCase))
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
    internal void ApplyRelease(UpdateState state, GitHubRelease? release, UpdateChannel channel)
    {
        // A successful lookup that found nothing usable. Nothing to offer and nothing known about the
        // channel's newest version, which is different from knowing it is behind us.
        if (release is null)
        {
            ClearAvailableUpdate(state);
            return;
        }

        if (release.IsDraft)
        {
            _logger.LogInformation("Latest release {Tag} is a draft - not offering it", release.TagName);
            ClearAvailableUpdate(state);
            return;
        }

        // Refused on stable, wanted on development. This is the entire behavioural difference between
        // the two channels, and it is one condition rather than a second code path on purpose.
        if (release.IsPrerelease && channel != UpdateChannel.Development)
        {
            _logger.LogInformation("Latest release {Tag} is a pre-release - not offering it on the stable channel", release.TagName);
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
        // ticked - a very easy mistake to make when publishing. On stable that is still a refusal, so
        // the flag being missed cannot leak a pre-release to somebody who never asked for one.
        if (released.IsPrerelease && channel != UpdateChannel.Development)
        {
            _logger.LogInformation("Latest release tag {Tag} is a pre-release version - not offering it on the stable channel", release.TagName);
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
        // something this offers, on any channel. The case worth naming is the second one: somebody
        // running a development build who switched back to stable is genuinely AHEAD of the newest
        // stable release, and telling them "you are on the latest version" would be a lie while
        // offering them the older release would be a downgrade dressed as an update. So nothing is
        // offered, and the fact is recorded so the UI can say it plainly.
        if (released <= current)
        {
            ClearAvailableUpdate(state);
            state.ChannelNewestVersion = released.ToString();
            state.AheadOfChannel = current > released;
            return;
        }

        var installer = SelectInstallerAsset(release.Assets);
        var checksum = installer is null ? null : SelectChecksumAsset(release.Assets, installer);

        state.LatestVersion = released.ToString();
        state.ChannelNewestVersion = released.ToString();
        state.AheadOfChannel = false;
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

    private UpdateStatusResponse FailDownload(UpdateState state, UpdateChannel channel, string message)
    {
        _downloadState = UpdateDownloadStates.Failed;
        _downloadMessage = message;
        return BuildStatus(state, channel);
    }

    private static void ClearAvailableUpdate(UpdateState state)
    {
        state.LatestVersion = null;
        state.ChannelNewestVersion = null;
        state.AheadOfChannel = false;
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

    private UpdateStatusResponse BuildStatus(UpdateState state, UpdateChannel channel)
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
            DownloadMessage: _downloadMessage,
            Channel: ChannelName(channel),

            // Only meaningful while the cached result actually belongs to the selected channel. A
            // switch clears the cache, so this reads false for the moment between switching and the
            // re-check landing - which is right: "you are ahead of stable" is a claim, and it should
            // not be made on the strength of a measurement taken against the development channel.
            AheadOfChannel: state.AheadOfChannel &&
                string.Equals(state.CheckedChannel, channel.ToString(), StringComparison.OrdinalIgnoreCase),
            ChannelNewestVersion: state.ChannelNewestVersion);
    }

    /// <summary>
    /// The channel as the API names it. Lower-case and written out here rather than left to whatever
    /// the enum's ToString happens to produce, because this crosses the wire into the SPA and a
    /// rename of an enum member has no business changing an API's vocabulary.
    /// </summary>
    internal static string ChannelName(UpdateChannel channel) =>
        channel == UpdateChannel.Development ? "development" : "stable";

    /// <summary>
    /// Parses the channel back off the wire. Anything unrecognised is refused outright rather than
    /// resolved to a default: this is a request somebody made, and quietly giving them a channel they
    /// did not name would be worse than telling the caller its value was wrong.
    /// <para>
    /// The two names are matched literally rather than through <c>Enum.TryParse</c>, which accepts
    /// numeric strings: <c>"1"</c> parses cleanly to <see cref="UpdateChannel.Development"/> and
    /// passes <c>Enum.IsDefined</c>, so a client sending an index - or a mistyped field, or a stray
    /// value from a form - would silently be moved onto development builds. That is the precise
    /// substitution this method exists to prevent, so it does not go near the enum parser.
    /// </para>
    /// </summary>
    internal static bool TryParseChannel(string? value, out UpdateChannel channel)
    {
        channel = UpdateChannel.Stable;
        var name = value?.Trim();

        if (string.Equals(name, "stable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(name, "development", StringComparison.OrdinalIgnoreCase))
        {
            channel = UpdateChannel.Development;
            return true;
        }

        return false;
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

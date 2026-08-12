namespace FSOps.Data.Import;

/// <summary>
/// Shared, thread-visible snapshot of where the world-data import is at. Registered as a
/// singleton so the status endpoint can report on it while the import runs on a background
/// task. Fields are simple value types written by one importer at a time, so plain volatile
/// reads/writes are enough - no locking needed for a status display.
///
/// <para><see cref="TryBegin"/> is the exception: it is a real compare-and-swap, because the
/// startup refresh and the manual "refresh world data" button in Settings can genuinely race.
/// Two importers running the same upsert concurrently would deadlock on SQLite's write lock at
/// best, so exactly one is allowed to start and the other is turned away.</para>
/// </summary>
public class WorldDataImportProgress
{
    private const int Idle = 0;
    private const int Busy = 1;

    private int _busy;
    private volatile bool _seeded;
    private volatile bool _importInProgress;
    private volatile bool _refreshInProgress;
    private int _airportCount;
    private int _runwayCount;
    private double _progressPercent;
    private volatile string? _dataVersion;
    private long _lastAppliedUtcTicks;

    public bool Seeded => _seeded;

    /// <summary>True while a first-time seed of an empty database is running.</summary>
    public bool ImportInProgress => _importInProgress;

    /// <summary>
    /// True while a refresh over already-seeded data is running. Deliberately separate from
    /// <see cref="ImportInProgress"/>: the dashboard banner treats that one as "the app has no
    /// airports yet", which is exactly what a refresh is not.
    /// </summary>
    public bool RefreshInProgress => _refreshInProgress;

    public bool IsBusy => Volatile.Read(ref _busy) == Busy;

    public int AirportCount => _airportCount;

    public int RunwayCount => _runwayCount;

    public double ProgressPercent => _progressPercent;

    /// <summary>Short identity of the bundled data currently in the database, or null if unknown.</summary>
    public string? DataVersion => _dataVersion;

    /// <summary>When the world data was last imported or refreshed, or null if unknown.</summary>
    public DateTimeOffset? LastAppliedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAppliedUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Claims the right to run an import. Returns false when one is already running, in which
    /// case the caller must not touch the database. Every successful call must be paired with
    /// <see cref="End"/> in a finally block.
    /// </summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _busy, Busy, Idle) == Idle;

    public void End() => Interlocked.Exchange(ref _busy, Idle);

    public void MarkAlreadySeeded(int airportCount, int runwayCount)
    {
        _airportCount = airportCount;
        _runwayCount = runwayCount;
        _seeded = true;
        _importInProgress = false;
        _refreshInProgress = false;
        _progressPercent = 100;
    }

    /// <summary>
    /// A first-time seed has started: there are no airports yet, and the UI should say so.
    /// </summary>
    public void MarkStarted()
    {
        _seeded = false;
        _importInProgress = true;
        _refreshInProgress = false;
        _progressPercent = 0;
    }

    /// <summary>
    /// A refresh over existing data has started. <see cref="Seeded"/> stays true throughout - the
    /// airports are all still there and every screen keeps working while this runs.
    /// </summary>
    public void MarkRefreshStarted()
    {
        _seeded = true;
        _importInProgress = false;
        _refreshInProgress = true;
        _progressPercent = 0;
    }

    public void SetProgressPercent(double percent)
    {
        _progressPercent = percent;
    }

    public void MarkCompleted(int airportCount, int runwayCount)
    {
        _airportCount = airportCount;
        _runwayCount = runwayCount;
        _seeded = true;
        _importInProgress = false;
        _refreshInProgress = false;
        _progressPercent = 100;
    }

    public void MarkFailed()
    {
        _importInProgress = false;
        _refreshInProgress = false;
        _progressPercent = 0;
    }

    /// <summary>Records which bundle the current rows came from, and when it was applied.</summary>
    public void SetVersion(string? shortId, DateTimeOffset? appliedUtc)
    {
        _dataVersion = shortId;
        Interlocked.Exchange(ref _lastAppliedUtcTicks, appliedUtc?.UtcTicks ?? 0);
    }
}

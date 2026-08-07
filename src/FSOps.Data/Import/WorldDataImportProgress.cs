namespace FSOps.Data.Import;

/// <summary>
/// Shared, thread-visible snapshot of where the world-data import is at. Registered as a
/// singleton so the status endpoint can report on it while the import runs on a background
/// task. Fields are simple value types written by one importer at a time, so plain volatile
/// reads/writes are enough - no locking needed for a status display.
/// </summary>
public class WorldDataImportProgress
{
    private volatile bool _seeded;
    private volatile bool _importInProgress;
    private int _airportCount;
    private int _runwayCount;
    private double _progressPercent;

    public bool Seeded => _seeded;

    public bool ImportInProgress => _importInProgress;

    public int AirportCount => _airportCount;

    public int RunwayCount => _runwayCount;

    public double ProgressPercent => _progressPercent;

    public void MarkAlreadySeeded(int airportCount, int runwayCount)
    {
        _airportCount = airportCount;
        _runwayCount = runwayCount;
        _seeded = true;
        _importInProgress = false;
        _progressPercent = 100;
    }

    public void MarkStarted()
    {
        _seeded = false;
        _importInProgress = true;
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
        _progressPercent = 100;
    }

    public void MarkFailed()
    {
        _importInProgress = false;
        _progressPercent = 0;
    }
}

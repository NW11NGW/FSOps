using FSOps.Server.Services;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// Update storage rooted in a throwaway directory. Every updater test uses this so a test run can
/// never read, write or delete anything under the real %LOCALAPPDATA%\FSOps - the updater deletes
/// files as part of its normal, correct behaviour (that is the whole point of the checksum path),
/// and pointing that at a live save would be exactly the kind of accident this project has already
/// paid for once.
/// </summary>
internal sealed class TempUpdateStorage : IUpdateStorage, IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();

    public TempUpdateStorage()
    {
        _root = Path.Combine(Path.GetTempPath(), "fsops-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string UpdatesDirectory => Path.Combine(_root, "updates");

    public string StateFilePath => Path.Combine(_root, UpdateStateFile.FileName);

    /// <summary>Number of times state has been persisted - lets a test prove a check really did
    /// write its result rather than only returning it.</summary>
    public int SaveCount { get; private set; }

    public UpdateState Load()
    {
        lock (_gate)
        {
            return UpdateStateFile.Read(StateFilePath);
        }
    }

    public void Save(UpdateState state)
    {
        lock (_gate)
        {
            SaveCount++;
            UpdateStateFile.Write(StateFilePath, state);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

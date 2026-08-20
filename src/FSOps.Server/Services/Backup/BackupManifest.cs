namespace FSOps.Server.Services.Backup;

/// <summary>
/// What a <c>.fsopsbak</c> file says about itself. Stored as <c>manifest.json</c> inside the
/// archive, beside the database copy it describes.
///
/// <para>Three fields here exist to stop a restore doing damage rather than to describe anything:
/// <see cref="DatabaseSha256"/> and <see cref="DatabaseBytes"/> catch a file that was truncated or
/// corrupted in transit, and <see cref="MigrationVersion"/> catches a backup taken by a newer build
/// of FSOps whose schema this one cannot read. The last of those is the trap: restoring a newer
/// save into an older app would not fail cleanly, it would half-work and then behave like a corrupt
/// database. Migrations only run forward, so the reverse - an older backup into a newer app - is
/// the supported direction and is expected to work.</para>
/// </summary>
public sealed class BackupManifest
{
    /// <summary>Marks the file as ours. Checked before anything else is trusted.</summary>
    public const string FormatMarker = "fsops-backup";

    /// <summary>The layout of the archive itself, not the database schema inside it.</summary>
    public const int CurrentFormatVersion = 1;

    public const string ManifestEntryName = "manifest.json";

    public const string DatabaseEntryName = "fsops.db";

    /// <summary>The file extension the player sees. A zip underneath, deliberately not named .zip:
    /// double-clicking a backup should not look like something to unpack by hand.</summary>
    public const string FileExtension = ".fsopsbak";

    public string Format { get; set; } = FormatMarker;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The build of FSOps that took the backup, for display and for the refusal message.</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// The newest EF migration applied to the database in this archive. The compatibility check
    /// reads the same value out of the database itself and prefers that; this copy exists so a
    /// refusal can still say something useful about a file whose database will not open.
    /// </summary>
    public string? MigrationVersion { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>The airline's name at the time of the backup, so a folder of these is readable.
    /// Null before an airline has been created.</summary>
    public string? AirlineName { get; set; }

    public string? AirlineIcaoCode { get; set; }

    public long DatabaseBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the database entry, verified on every restore.</summary>
    public string DatabaseSha256 { get; set; } = string.Empty;
}

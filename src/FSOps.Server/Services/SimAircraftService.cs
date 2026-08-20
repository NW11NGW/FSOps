using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Core.Time;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Everything the app knows about which aircraft the player can load in their simulator, in one
/// place: where their Community folder is, what a scan found there, which edition they own, and
/// what they have ticked by hand.
///
/// <para>This exists so that contract flying - jobs from other operators, flown in an aircraft the
/// contract supplies - can only ever name an aircraft the player actually has. A contract for
/// something not in their hangar is worse than no contract at all.</para>
///
/// <para>The scan is cached in the settings row rather than re-run per request. Walking somebody's
/// whole package folder is not free, and the answer changes only when they install something -
/// which is why there is a button for it.</para>
/// </summary>
public sealed class SimAircraftService
{
    private static readonly JsonSerializerOptions StorageJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FsOpsDbContext _db;
    private readonly InstalledAircraftScanner _scanner;
    private readonly IClock _clock;
    private readonly ILogger<SimAircraftService> _log;

    public SimAircraftService(
        FsOpsDbContext db,
        InstalledAircraftScanner scanner,
        IClock clock,
        ILogger<SimAircraftService> log)
    {
        _db = db;
        _scanner = scanner;
        _clock = clock;
        _log = log;
    }

    /// <summary>Reads the current picture without touching the disk.</summary>
    public async Task<SimAircraftState> GetAsync(Guid ownerUserId, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(ownerUserId, ct);
        await _db.SaveChangesAsync(ct);
        return Describe(settings);
    }

    /// <summary>
    /// Walks the player's simulator folders and stores what was found. Never throws for anything
    /// about the folder itself - a missing folder, the wrong folder or an unreadable one all come
    /// back as an outcome on the result.
    /// </summary>
    public async Task<SimAircraftState> ScanAsync(Guid ownerUserId, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(ownerUserId, ct);
        var folder = ResolveCommunityFolder(settings);

        var result = _scanner.Scan(folder, _clock.UtcNow);
        settings.SimAircraftScanJson = JsonSerializer.Serialize(result, StorageJson);

        // A scan that succeeded against an auto-detected folder writes the path down, so the next
        // scan does not have to find it again and so the player can see which folder FSOps read.
        // A failed scan never overwrites a path they set themselves.
        if (result.Outcome == AircraftScanOutcome.Scanned && string.IsNullOrWhiteSpace(settings.CommunityFolderPath))
        {
            settings.CommunityFolderPath = folder;
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Sim aircraft scan finished: {Outcome}, {Packages} packages inspected, {Identified} aircraft identified.",
            result.Outcome,
            result.PackagesInspected,
            result.IdentifiedTypeDesignators.Count);

        return Describe(settings);
    }

    /// <summary>Stores the edition and, optionally, an explicit Community folder path.</summary>
    public async Task<SimAircraftState> UpdateAsync(
        Guid ownerUserId,
        SimEdition edition,
        string? communityFolderPath,
        bool clearCommunityFolderPath,
        CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(ownerUserId, ct);
        settings.SimEdition = edition;

        if (clearCommunityFolderPath)
        {
            settings.CommunityFolderPath = null;
        }
        else if (communityFolderPath is not null)
        {
            var trimmed = communityFolderPath.Trim();
            settings.CommunityFolderPath = trimmed.Length == 0 ? null : trimmed;
        }

        await _db.SaveChangesAsync(ct);
        return Describe(settings);
    }

    /// <summary>
    /// Ticks one aircraft on or off by hand, or clears the tick so FSOps goes back to its own
    /// answer. Only ever stores the ticks that actually disagree with what FSOps worked out - a
    /// tick that agrees is dropped, so somebody who later installs the add-on, or corrects their
    /// edition, does not carry a stale override that quietly outranks the truth.
    /// </summary>
    public async Task<SimAircraftState> SetOverrideAsync(
        Guid ownerUserId,
        string typeDesignator,
        bool? available,
        CancellationToken ct)
    {
        var aircraft = ContractAircraftCatalogue.Find(typeDesignator)
            ?? throw new ArgumentException($"'{typeDesignator}' is not an aircraft FSOps knows about.", nameof(typeDesignator));

        var settings = await GetOrCreateAsync(ownerUserId, ct);
        var overrides = ReadOverrides(settings);

        var on = new HashSet<string>(overrides.On, StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(overrides.Off, StringComparer.OrdinalIgnoreCase);
        on.Remove(aircraft.TypeDesignator);
        off.Remove(aircraft.TypeDesignator);

        if (available.HasValue)
        {
            var withoutOverride = ContractAircraftAvailabilityResolver
                .Resolve(settings.SimEdition, ReadScan(settings), new ContractAircraftOverrides(on, off))
                .Single(r => r.Aircraft.TypeDesignator == aircraft.TypeDesignator);

            if (withoutOverride.Available != available.Value)
            {
                (available.Value ? on : off).Add(aircraft.TypeDesignator);
            }
        }

        settings.SimAircraftOverridesJson = on.Count == 0 && off.Count == 0
            ? null
            : JsonSerializer.Serialize(
                new ContractAircraftOverrides(on.OrderBy(d => d, StringComparer.Ordinal).ToList(), off.OrderBy(d => d, StringComparer.Ordinal).ToList()),
                StorageJson);

        await _db.SaveChangesAsync(ct);
        return Describe(settings);
    }

    /// <summary>
    /// The Community folder FSOps will actually read: whatever the player configured, or the best
    /// one found automatically. Null when neither exists, which means "could not find it" and never
    /// "you have no add-ons".
    /// </summary>
    public static string? ResolveCommunityFolder(UserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CommunityFolderPath))
        {
            return settings.CommunityFolderPath.Trim();
        }

        return SimInstallLocator.FindCommunityFolders().FirstOrDefault();
    }

    private SimAircraftState Describe(UserSettings settings)
    {
        var scan = ReadScan(settings);
        var overrides = ReadOverrides(settings);
        var aircraft = ContractAircraftAvailabilityResolver.Resolve(settings.SimEdition, scan, overrides);

        return new SimAircraftState(
            settings.SimEdition,
            settings.CommunityFolderPath,
            ResolveCommunityFolder(settings),
            scan,
            aircraft);
    }

    private AircraftScanResult? ReadScan(UserSettings settings) =>
        Deserialise<AircraftScanResult>(settings.SimAircraftScanJson, nameof(settings.SimAircraftScanJson));

    private ContractAircraftOverrides ReadOverrides(UserSettings settings) =>
        Deserialise<ContractAircraftOverrides>(settings.SimAircraftOverridesJson, nameof(settings.SimAircraftOverridesJson))
        ?? ContractAircraftOverrides.Empty;

    /// <summary>
    /// Reads one of the JSON columns. Unreadable JSON is treated as "not set" and logged, never
    /// thrown: a settings row that cannot be parsed must not be able to take the settings page down,
    /// and the worst this costs is a scan the player can re-run with a button.
    /// </summary>
    private T? Deserialise<T>(string? json, string column) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, StorageJson);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Could not read stored {Column}; treating it as not set.", column);
            return null;
        }
    }

    private async Task<UserSettings> GetOrCreateAsync(Guid ownerUserId, CancellationToken ct)
    {
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ownerUserId };
        _db.UserSettings.Add(settings);
        return settings;
    }
}

/// <param name="Edition">The edition the player says they own.</param>
/// <param name="ConfiguredCommunityFolderPath">What they set by hand, or null.</param>
/// <param name="EffectiveCommunityFolderPath">What FSOps will actually read, configured or found.</param>
/// <param name="LastScan">The last scan, or null when none has been run.</param>
/// <param name="Aircraft">Every catalogue aircraft, with whether a contract may name it and why.</param>
public sealed record SimAircraftState(
    SimEdition Edition,
    string? ConfiguredCommunityFolderPath,
    string? EffectiveCommunityFolderPath,
    AircraftScanResult? LastScan,
    IReadOnlyList<ResolvedContractAircraft> Aircraft);

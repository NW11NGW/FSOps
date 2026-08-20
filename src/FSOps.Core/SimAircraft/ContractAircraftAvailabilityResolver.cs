namespace FSOps.Core.SimAircraft;

/// <summary>
/// Why FSOps believes the player can - or cannot - load a particular aircraft. Shown next to every
/// entry in the tick list, because "we found this in your Community folder" and "your edition
/// includes this" are very different claims and the player is the one who can tell which is right.
/// </summary>
public enum AircraftAvailabilityEvidence
{
    /// <summary>Nothing suggests the player has it: not base content for their edition, not found.</summary>
    NotIncluded = 0,

    /// <summary>Their simulator edition ships it. Believed, not proved.</summary>
    Edition = 1,

    /// <summary>A base-content package for it is on disk. Proof it is there.</summary>
    InstalledOnDisk = 2,

    /// <summary>Found as an add-on in the Community folder. The strongest evidence there is.</summary>
    CommunityFolder = 3,

    /// <summary>The player ticked it themselves. Overrides everything - they know, FSOps guesses.</summary>
    TickedOn = 4,

    /// <summary>The player unticked it themselves. Also overrides everything.</summary>
    TickedOff = 5,
}

/// <summary>The aircraft the player has ticked on or off by hand, over the top of everything else.</summary>
/// <param name="On">Ticked on despite FSOps not finding them.</param>
/// <param name="Off">Ticked off despite FSOps believing they are there.</param>
public sealed record ContractAircraftOverrides(
    IReadOnlyCollection<string> On,
    IReadOnlyCollection<string> Off)
{
    public static ContractAircraftOverrides Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>());
}

/// <param name="Aircraft">The catalogue entry.</param>
/// <param name="Available">Whether a contract may name this aircraft.</param>
/// <param name="Evidence">Why.</param>
public sealed record ResolvedContractAircraft(
    ContractAircraft Aircraft,
    bool Available,
    AircraftAvailabilityEvidence Evidence);

/// <summary>
/// Turns "which edition did you buy", "here is what we found on your disk" and "here is what you
/// ticked" into the one answer contract generation needs: may a contract name this aircraft?
///
/// <para><b>The precedence is the whole design.</b> The player's own tick beats everything, because
/// they are the only party here who actually knows. Below that, evidence beats belief: a package
/// found on disk outranks an assumption from the edition setting. And a scan that did not find
/// something never subtracts - it only ever adds - because MSFS 2024 streams most base content, so
/// an aircraft somebody owns but has not flown leaves nothing behind to find. Letting a scan miss
/// remove an aircraft would take away the Cessna 172 of every player who has not yet flown one.</para>
/// </summary>
public static class ContractAircraftAvailabilityResolver
{
    public static IReadOnlyList<ResolvedContractAircraft> Resolve(
        SimEdition edition,
        AircraftScanResult? scan,
        ContractAircraftOverrides? overrides)
    {
        var on = ToSet(overrides?.On);
        var off = ToSet(overrides?.Off);

        var inCommunity = ToSet(scan?.AircraftPackages
            .Select(p => p.TypeDesignator)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!));

        var onDisk = ToSet(scan?.BasePackageTypeDesignators);

        return ContractAircraftCatalogue.All
            .Select(aircraft => Resolve(aircraft, edition, inCommunity, onDisk, on, off))
            .ToList();
    }

    /// <summary>The designators a contract may currently be written for, upper-case, sorted.</summary>
    public static IReadOnlyList<string> AvailableDesignators(
        SimEdition edition,
        AircraftScanResult? scan,
        ContractAircraftOverrides? overrides) =>
        Resolve(edition, scan, overrides)
            .Where(r => r.Available)
            .Select(r => r.Aircraft.TypeDesignator)
            .ToList();

    private static ResolvedContractAircraft Resolve(
        ContractAircraft aircraft,
        SimEdition edition,
        ISet<string> inCommunity,
        ISet<string> onDisk,
        ISet<string> tickedOn,
        ISet<string> tickedOff)
    {
        var designator = aircraft.TypeDesignator;

        if (tickedOff.Contains(designator))
        {
            return new ResolvedContractAircraft(aircraft, false, AircraftAvailabilityEvidence.TickedOff);
        }

        if (tickedOn.Contains(designator))
        {
            return new ResolvedContractAircraft(aircraft, true, AircraftAvailabilityEvidence.TickedOn);
        }

        if (inCommunity.Contains(designator))
        {
            return new ResolvedContractAircraft(aircraft, true, AircraftAvailabilityEvidence.CommunityFolder);
        }

        if (onDisk.Contains(designator))
        {
            return new ResolvedContractAircraft(aircraft, true, AircraftAvailabilityEvidence.InstalledOnDisk);
        }

        if (edition.Includes(aircraft.ShipsWith))
        {
            return new ResolvedContractAircraft(aircraft, true, AircraftAvailabilityEvidence.Edition);
        }

        return new ResolvedContractAircraft(aircraft, false, AircraftAvailabilityEvidence.NotIncluded);
    }

    private static ISet<string> ToSet(IEnumerable<string>? values) =>
        new HashSet<string>(values ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
}

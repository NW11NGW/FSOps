using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Core.Time;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Keeps the board of offered contracts in step with the deterministic generator.
///
/// <para><b>The board is generated, then persisted - not persisted, then mutated.</b> Reading the
/// board for a period that has already been generated returns exactly the rows written the first
/// time; reading it for a new period generates that period's jobs, writes them, and expires the ones
/// the board has moved past. Accepting a contract is what takes it out of that cycle: an accepted job
/// belongs to the player and survives every refresh until it is finished, abandoned, or its deadline
/// passes.</para>
///
/// <para>The consequence worth stating: <b>the board cannot be rerolled.</b> Reloading the page, or
/// restarting the app, produces the same jobs, because the jobs are a function of the world seed, the
/// airline and the period rather than of when somebody happened to look. That is what makes it a
/// board rather than a lever.</para>
/// </summary>
public sealed class ContractBoardService
{
    /// <summary>
    /// How many airports may be handed to the generator as destinations and intermediate stops.
    /// Enough that the whole world is genuinely reachable; bounded so one board read can never turn
    /// into an unbounded scan of a table with tens of thousands of rows in it.
    /// </summary>
    private const int MaxCandidateAirports = 12_000;

    /// <summary>
    /// Runways shorter than this belong to nothing a contract will ever name, so filtering them out
    /// up front keeps the candidate list to airports that can actually be used. Below the smallest
    /// minimum any category asks for, so this never removes an option the generator would have taken.
    /// </summary>
    private const int MinimumUsableRunwayFt = 1_500;

    private readonly FsOpsDbContext _db;
    private readonly SimAircraftService _simAircraft;
    private readonly EconomyConfigCatalog _economyConfigCatalog;
    private readonly IClock _clock;
    private readonly ILogger<ContractBoardService> _log;

    public ContractBoardService(
        FsOpsDbContext db,
        SimAircraftService simAircraft,
        EconomyConfigCatalog economyConfigCatalog,
        IClock clock,
        ILogger<ContractBoardService> log)
    {
        _db = db;
        _simAircraft = simAircraft;
        _economyConfigCatalog = economyConfigCatalog;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// The current board: every offer for this period, plus every contract the player has accepted
    /// and not yet finished. Generates and persists the period's offers on first read.
    /// </summary>
    public async Task<ContractBoardState> GetBoardAsync(Airline airline, Guid ownerUserId, CancellationToken ct)
    {
        var config = _economyConfigCatalog.Get(airline.Playstyle).Contracts;
        var now = _clock.UtcNow;
        var bucket = ContractBoardKey.BucketFor(now, config.BoardRefreshHours);

        await ExpireOverdueAcceptedAsync(airline, config, now, ct);

        var existing = await _db.Contracts
            .Where(c => c.AirlineId == airline.Id && c.BoardBucket == bucket && c.Status == ContractStatus.Offered)
            .Include(c => c.Legs)
            .ToListAsync(ct);

        ContractBoardLimitation limitation;

        if (existing.Count > 0)
        {
            // Already generated for this period. Return what was written rather than regenerating -
            // the generator would produce the same jobs, but the persisted rows carry the identities
            // the player may already have accepted against.
            limitation = await DescribeExistingAsync(airline, ownerUserId, config, existing.Count, ct);
        }
        else
        {
            var board = await GenerateAsync(airline, ownerUserId, config, bucket, ct);
            existing = Persist(airline, board, now);
            limitation = board.Limitation;

            // Offers from an earlier period are gone the moment a new board exists. Only OFFERED ones:
            // an accepted contract has left the board and belongs to the player.
            var expired = await _db.Contracts
                .Where(c => c.AirlineId == airline.Id && c.BoardBucket < bucket && c.Status == ContractStatus.Offered)
                .ToListAsync(ct);

            foreach (var contract in expired)
            {
                contract.Status = ContractStatus.Expired;
                contract.ClosedUtc = now;
                contract.ClosedReason = "The board moved on before this job was accepted.";
            }

            if (expired.Count > 0 || existing.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            _log.LogInformation(
                "Generated contract board bucket {Bucket} for airline {AirlineId}: {Offered} offered, {Expired} expired.",
                bucket, airline.Id, existing.Count, expired.Count);
        }

        var accepted = await _db.Contracts
            .Where(c => c.AirlineId == airline.Id && c.Status == ContractStatus.Accepted)
            .Include(c => c.Legs)
            .ToListAsync(ct);

        return new ContractBoardState(
            bucket,
            ContractBoardKey.StartOf(bucket, config.BoardRefreshHours).AddHours(config.BoardRefreshHours),
            existing.OrderBy(c => c.BoardSlot).ToList(),
            accepted.OrderBy(c => c.AcceptedUtc ?? c.CreatedUtc).ToList(),
            limitation);
    }

    /// <summary>
    /// Marks accepted contracts whose deadline has passed with legs outstanding as abandoned, and
    /// raises the charge for the legs never flown.
    ///
    /// <para>Run lazily, on every board read, rather than by a background pass. That is deliberate:
    /// the deadline is generous and fixed at generation, so nothing here can surprise the player - by
    /// the time they see it, the date they were shown before they accepted has genuinely gone by.
    /// </para>
    /// </summary>
    private async Task ExpireOverdueAcceptedAsync(
        Airline airline, ContractConfig config, DateTimeOffset now, CancellationToken ct)
    {
        // Materialised before the date comparison - the SQLite provider cannot translate a
        // DateTimeOffset comparison (see this project's EF/SQLite notes). Filtered to Accepted first,
        // so this loads the handful of jobs the player currently has, never the whole history.
        var overdue = (await _db.Contracts
                .Where(c => c.AirlineId == airline.Id && c.Status == ContractStatus.Accepted)
                .ToListAsync(ct))
            .Where(c => c.DeadlineUtc < now)
            .ToList();

        if (overdue.Count == 0)
        {
            return;
        }

        foreach (var contract in overdue)
        {
            await ContractEconomicsPoster.PostAbandonAsync(
                _db, contract, config, now,
                $"The deadline of {contract.DeadlineUtc:yyyy-MM-dd} passed with legs still to fly.",
                ct);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("{Count} accepted contract(s) passed their deadline for airline {AirlineId}.", overdue.Count, airline.Id);
    }

    private async Task<ContractBoard> GenerateAsync(
        Airline airline, Guid ownerUserId, ContractConfig config, long bucket, CancellationToken ct)
    {
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(_db, ct);
        var origins = await ResolveOriginsAsync(airline, ct);
        var candidates = await ResolveCandidatesAsync(ct);
        var aircraft = await ResolveAvailableAircraftAsync(ownerUserId, ct);

        var request = new ContractBoardRequest(
            worldSeed, airline.Id, bucket, ContractBoardKey.StartOf(bucket, config.BoardRefreshHours),
            origins, candidates, aircraft);

        return ContractGenerator.Generate(config, request);
    }

    /// <summary>
    /// Airports the airline already touches: the hub, both ends of every active route, and wherever
    /// its aircraft are actually parked.
    ///
    /// <para>This is the user's own decision and it is the asymmetry that makes the feature work -
    /// jobs <b>start</b> where the player's network reaches and <b>end</b> anywhere at all. Without
    /// it a board would offer jobs on the other side of the world that the player would have to get
    /// to first; with it, every job on the board starts somewhere they have a reason to be.</para>
    /// </summary>
    private async Task<List<ContractAirport>> ResolveOriginsAsync(Airline airline, CancellationToken ct)
    {
        var icaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(airline.HomeAirportIcao))
        {
            icaos.Add(airline.HomeAirportIcao);
        }

        foreach (var route in await _db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct))
        {
            icaos.Add(route.DepartureIcao);
            icaos.Add(route.ArrivalIcao);
        }

        foreach (var location in await _db.FleetAircraft
                     .Where(f => f.AirlineId == airline.Id)
                     .Select(f => f.LocationIcao)
                     .ToListAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(location))
            {
                icaos.Add(location);
            }
        }

        var list = icaos.ToList();
        var airports = await _db.Airports.Where(a => list.Contains(a.Icao)).ToListAsync(ct);

        return airports
            .Select(ToContractAirport)
            // Ordered so the generator's own draws are reproducible whatever order EF returned rows in.
            .OrderBy(a => a.Icao, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<ContractAirport>> ResolveCandidatesAsync(CancellationToken ct)
    {
        var airports = await _db.Airports
            .Where(a => a.LongestRunwayFt >= MinimumUsableRunwayFt)
            .Where(a => a.SizeCategory == AirportSizeCategory.Large ||
                        a.SizeCategory == AirportSizeCategory.Medium ||
                        a.SizeCategory == AirportSizeCategory.Small)
            .OrderBy(a => a.Icao)
            .Take(MaxCandidateAirports)
            .ToListAsync(ct);

        return airports.Select(ToContractAirport).ToList();
    }

    /// <summary>
    /// The aircraft a contract may name - and it is a hard gate, not a preference. A contract for an
    /// aeroplane the player cannot load is worse than no contract at all, so the whole board is drawn
    /// from what <see cref="ContractAircraftAvailabilityResolver"/> says they have.
    /// </summary>
    private async Task<List<ContractAircraft>> ResolveAvailableAircraftAsync(Guid ownerUserId, CancellationToken ct)
    {
        var state = await _simAircraft.GetAsync(ownerUserId, ct);
        return state.Aircraft.Where(a => a.Available).Select(a => a.Aircraft).ToList();
    }

    private List<Contract> Persist(Airline airline, ContractBoard board, DateTimeOffset now)
    {
        var contracts = new List<Contract>(board.Contracts.Count);

        foreach (var generated in board.Contracts)
        {
            var contract = new Contract
            {
                Id = Guid.NewGuid(),
                AirlineId = airline.Id,
                Kind = generated.Kind,
                Status = ContractStatus.Offered,
                BoardBucket = board.Bucket,
                BoardSlot = generated.Slot,
                OperatorName = generated.OperatorName,
                AircraftTypeDesignator = generated.Aircraft.TypeDesignator,
                LoadDescription = generated.LoadDescription,
                PayloadKg = generated.PayloadKg,
                PaxCount = generated.PaxCount,
                Fee = generated.Fee,
                TotalDistanceNm = generated.TotalDistanceNm,
                TotalPlannedBlockMinutes = generated.TotalPlannedBlockMinutes,
                OfferedUtc = generated.OfferedUtc,
                DeadlineUtc = generated.DeadlineUtc,
                CreatedUtc = now,
            };

            for (var i = 0; i < generated.Legs.Count; i++)
            {
                var leg = generated.Legs[i];
                contract.Legs.Add(new ContractLeg
                {
                    Id = Guid.NewGuid(),
                    ContractId = contract.Id,
                    Sequence = leg.Sequence,
                    DepartureIcao = leg.Departure.Icao,
                    ArrivalIcao = leg.Arrival.Icao,
                    DistanceNm = leg.DistanceNm,
                    PlannedBlockMinutes = leg.PlannedBlockMinutes,
                    FeeShare = generated.FeeShares[i],
                });
            }

            contracts.Add(contract);
        }

        if (contracts.Count > 0)
        {
            _db.Contracts.AddRange(contracts);
        }

        return contracts;
    }

    /// <summary>
    /// Rebuilds the "why is this board thin" explanation for a period that was already generated.
    /// Regenerating just to read the limitation would be wasteful, and the two inputs it depends on -
    /// how many aircraft are available and how many airports the airline touches - are cheap to
    /// re-read and are what the player would act on anyway.
    /// </summary>
    private async Task<ContractBoardLimitation> DescribeExistingAsync(
        Airline airline, Guid ownerUserId, ContractConfig config, int generated, CancellationToken ct)
    {
        var aircraftCount = (await ResolveAvailableAircraftAsync(ownerUserId, ct)).Count;
        var originCount = (await ResolveOriginsAsync(airline, ct)).Count;

        return new ContractBoardLimitation(
            aircraftCount,
            originCount,
            config.BoardSize,
            generated,
            generated >= config.BoardSize
                ? null
                : $"Only {generated} of {config.BoardSize} jobs could be offered. {aircraftCount} aircraft " +
                  "are available for contract work, and every leg of every job has to be within range of the " +
                  "aircraft it names - so a short list, or an airline that only touches a few airports, makes " +
                  "for a thinner board. Ticking more aircraft in Settings is the quickest fix.");
    }

    private static ContractAirport ToContractAirport(Airport a) => new(
        a.Icao, a.Name, a.Municipality, a.Country, a.Latitude, a.Longitude, a.LongestRunwayFt, a.SizeCategory);
}

/// <param name="Bucket">Which board period this is.</param>
/// <param name="RefreshesUtc">When the board next changes. Shown so the player knows what they are choosing between.</param>
/// <param name="Offered">Jobs currently on the board.</param>
/// <param name="Accepted">Jobs the player has taken and not yet finished. These survive every refresh.</param>
public sealed record ContractBoardState(
    long Bucket,
    DateTimeOffset RefreshesUtc,
    IReadOnlyList<Contract> Offered,
    IReadOnlyList<Contract> Accepted,
    ContractBoardLimitation Limitation);

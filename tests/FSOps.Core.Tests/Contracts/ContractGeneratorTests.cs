using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.Contracts;

/// <summary>
/// The board generator: random in outcome, deterministic in mechanism, and never able to offer a job
/// the player cannot fly.
///
/// <para>The fixture is a spread of real airports across Europe, the North Atlantic and North America
/// so that distances, and therefore leg counts, are the ones real geography produces. Made-up
/// coordinates would let the generator look right while being wrong about the only thing that
/// matters.</para>
/// </summary>
public class ContractGeneratorTests
{
    private static readonly ContractConfig Config = new();

    private static ContractAirport A(string icao, double lat, double lon, int runwayFt,
        AirportSizeCategory size = AirportSizeCategory.Medium) =>
        new(icao, icao, icao, "XX", lat, lon, runwayFt, size);

    private static List<ContractAirport> World() =>
    [
        // British Isles
        A("EGGD", 51.383, -2.719, 8_000), A("EGPH", 55.950, -3.373, 8_400), A("EGPF", 55.872, -4.433, 8_700),
        A("EGLL", 51.470, -0.454, 12_800, AirportSizeCategory.Large), A("EGPC", 58.459, -3.093, 5_900, AirportSizeCategory.Small),
        A("EGPO", 58.216, -6.331, 7_200, AirportSizeCategory.Small), A("EGJJ", 49.208, -2.195, 5_597, AirportSizeCategory.Small),
        A("EGNS", 54.083, -4.624, 5_754, AirportSizeCategory.Small), A("EGAA", 54.658, -6.216, 9_121),
        A("EIDW", 53.421, -6.270, 8_652, AirportSizeCategory.Large),
        // Continental Europe
        A("LFPG", 49.010, 2.548, 13_829, AirportSizeCategory.Large), A("EHAM", 52.309, 4.764, 12_467, AirportSizeCategory.Large),
        A("EDDF", 50.033, 8.571, 13_123, AirportSizeCategory.Large), A("LEMD", 40.472, -3.561, 14_272, AirportSizeCategory.Large),
        A("LIRF", 41.800, 12.239, 12_795, AirportSizeCategory.Large), A("ENGM", 60.194, 11.100, 11_811),
        A("ESSA", 59.652, 17.919, 10_827), A("EKCH", 55.618, 12.656, 11_811), A("LPPT", 38.774, -9.134, 12_484),
        A("LGAV", 37.936, 23.947, 13_123), A("BIRK", 64.130, -21.941, 6_120), A("BIKF", 63.985, -22.605, 10_000),
        A("EKVG", 62.064, -7.277, 5_910, AirportSizeCategory.Small),
        // Greenland / Canada / US
        A("BGKK", 65.574, -37.123, 3_900, AirportSizeCategory.Small), A("BGBW", 61.160, -45.426, 6_004, AirportSizeCategory.Small),
        A("BGGH", 64.191, -51.678, 3_117, AirportSizeCategory.Small), A("CYFB", 63.756, -68.556, 8_605, AirportSizeCategory.Small),
        A("CYVP", 58.096, -68.427, 6_000, AirportSizeCategory.Small), A("CYYR", 53.319, -60.426, 11_051),
        A("CYQX", 48.937, -54.568, 10_500), A("CYHZ", 44.881, -63.509, 10_500),
        A("KJFK", 40.640, -73.779, 14_511, AirportSizeCategory.Large), A("KBOS", 42.363, -71.006, 10_083, AirportSizeCategory.Large),
        A("CYYZ", 43.677, -79.631, 11_120, AirportSizeCategory.Large),
        // Further afield, so an "Epic" band has somewhere to reach
        A("KLAX", 33.942, -118.408, 12_091, AirportSizeCategory.Large), A("KMIA", 25.793, -80.291, 13_016, AirportSizeCategory.Large),
        A("OMDB", 25.253, 55.365, 13_124, AirportSizeCategory.Large), A("FACT", -33.965, 18.602, 10_502, AirportSizeCategory.Large),
    ];

    private static List<ContractAirport> Origins() =>
        World().Where(a => a.Icao is "EGGD" or "EGPH" or "EGLL").ToList();

    /// <summary>A representative slice of the real catalogue, spanning the whole size range.</summary>
    private static List<ContractAircraft> Aircraft(params string[] designators) =>
        designators.Select(d => ContractAircraftCatalogue.Find(d)!).ToList();

    private static List<ContractAircraft> WideSelection() =>
        Aircraft("C172", "SR22", "BE58", "TBM9", "C208", "PC12", "C25C", "AT72", "DH8D", "A20N", "B38M");

    private static ContractBoardRequest Request(
        IReadOnlyList<ContractAircraft>? aircraft = null,
        IReadOnlyList<ContractAirport>? origins = null,
        long bucket = 20_400,
        int worldSeed = 4_242) =>
        new(worldSeed, Guid.Parse("11111111-2222-3333-4444-555555555555"), bucket,
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            origins ?? Origins(), World(), aircraft ?? WideSelection());

    // ---------- Determinism ----------

    /// <summary>
    /// <b>The same airline in the same state produces the same board twice.</b> Not "a board of the
    /// same shape" - the same jobs, the same operators, the same aircraft, the same chain of stops,
    /// the same fee, to the penny.
    ///
    /// <para>This is what makes the board a board rather than a lever. Without it, the rational play
    /// is to keep reloading until something good appears, and none of this could be tested at all.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSameRequest_ProducesAnIdenticalBoard()
    {
        var first = ContractGenerator.Generate(Config, Request());
        var second = ContractGenerator.Generate(Config, Request());

        Assert.Equal(first.Contracts.Count, second.Contracts.Count);
        Assert.NotEmpty(first.Contracts);

        foreach (var (a, b) in first.Contracts.Zip(second.Contracts))
        {
            Assert.Equal(a.Slot, b.Slot);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.OperatorName, b.OperatorName);
            Assert.Equal(a.Aircraft.TypeDesignator, b.Aircraft.TypeDesignator);
            Assert.Equal(a.Fee, b.Fee);
            Assert.Equal(a.PayloadKg, b.PayloadKg);
            Assert.Equal(a.PaxCount, b.PaxCount);
            Assert.Equal(a.LoadDescription, b.LoadDescription);
            Assert.Equal(a.DeadlineUtc, b.DeadlineUtc);
            Assert.Equal(
                a.Legs.Select(l => $"{l.Departure.Icao}-{l.Arrival.Icao}"),
                b.Legs.Select(l => $"{l.Departure.Icao}-{l.Arrival.Icao}"));
            Assert.Equal(a.FeeShares, b.FeeShares);
        }
    }

    /// <summary>
    /// Deterministic must not mean identical for everybody. A different world seed, a different
    /// airline or a different period each has to give a genuinely different board, or "deterministic"
    /// would just be another word for "fixed".
    /// </summary>
    [Fact]
    public void ADifferentSeedAirlineOrPeriod_ProducesADifferentBoard()
    {
        string Signature(ContractBoard board) => string.Join("|", board.Contracts.Select(
            c => $"{c.Kind}:{c.Aircraft.TypeDesignator}:{c.Legs[0].Departure.Icao}>{c.Legs[^1].Arrival.Icao}:{c.Fee}"));

        var baseline = Signature(ContractGenerator.Generate(Config, Request()));

        var otherSeed = Signature(ContractGenerator.Generate(Config, Request(worldSeed: 99)));
        var otherPeriod = Signature(ContractGenerator.Generate(Config, Request(bucket: 20_401)));
        var otherAirline = Signature(ContractGenerator.Generate(
            Config,
            Request() with { AirlineId = Guid.Parse("99999999-8888-7777-6666-555555555555") }));

        Assert.NotEqual(baseline, otherSeed);
        Assert.NotEqual(baseline, otherPeriod);
        Assert.NotEqual(baseline, otherAirline);
    }

    // ---------- The promise the board makes ----------

    /// <summary>
    /// <b>Every generated leg is within range of the aircraft the contract names.</b> Checked across
    /// many periods rather than one, because a single board could pass by luck and this is the claim
    /// the whole feature rests on: a job on the board is always flyable by the aeroplane on it.
    /// </summary>
    [Fact]
    public void EveryLegOfEveryJob_IsWithinTheNamedAircraftsRange()
    {
        var checkedLegs = 0;

        for (var bucket = 20_000L; bucket < 20_120L; bucket++)
        {
            var board = ContractGenerator.Generate(Config, Request(bucket: bucket));

            foreach (var contract in board.Contracts)
            {
                var operationalRangeNm = RouteRangeAssessor.OperationalRangeNm(contract.Aircraft.RangeNm);

                foreach (var leg in contract.Legs)
                {
                    checkedLegs++;
                    Assert.True(
                        leg.DistanceNm <= operationalRangeNm,
                        $"{contract.Aircraft.TypeDesignator} was given {leg.Departure.Icao}-{leg.Arrival.Icao} at " +
                        $"{leg.DistanceNm:F0} nm, beyond its {operationalRangeNm:F0} nm operational range.");
                }
            }
        }

        Assert.True(checkedLegs > 500, $"Only {checkedLegs} legs were examined - the sweep is not proving much.");
    }

    /// <summary>Legs are contiguous and numbered from one: the aeroplane is always where the next sector expects it.</summary>
    [Fact]
    public void LegsAreContiguousAndSequential()
    {
        for (var bucket = 20_000L; bucket < 20_040L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                Assert.Equal(Enumerable.Range(1, contract.Legs.Count), contract.Legs.Select(l => l.Sequence));

                for (var i = 1; i < contract.Legs.Count; i++)
                {
                    Assert.Equal(contract.Legs[i - 1].Arrival.Icao, contract.Legs[i].Departure.Icao);
                }
            }
        }
    }

    /// <summary>
    /// <b>Contracts only ever name aircraft the player can actually load.</b> A contract for something
    /// not in their hangar is worse than no contract at all, so this is a hard gate rather than a
    /// preference - and it is tested with a deliberately tiny list, where a leak would be obvious.
    /// </summary>
    [Fact]
    public void OnlyAircraftFromTheAvailableList_AreEverNamed()
    {
        var available = Aircraft("C172", "TBM9");
        var allowed = available.Select(a => a.TypeDesignator).ToHashSet();

        for (var bucket = 20_000L; bucket < 20_060L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(available, bucket: bucket)).Contracts)
            {
                Assert.Contains(contract.Aircraft.TypeDesignator, allowed);
            }
        }
    }

    /// <summary>
    /// Jobs start where the airline already reaches; they may end anywhere. That asymmetry is the
    /// user's own decision and it is what keeps the board grounded in the network they built while
    /// still allowing a transatlantic ferry.
    /// </summary>
    [Fact]
    public void EveryJobStartsAtAnAirportTheAirlineTouches_AndMayEndAnywhere()
    {
        var origins = Origins().Select(o => o.Icao).ToHashSet();
        var destinations = new HashSet<string>();

        for (var bucket = 20_000L; bucket < 20_060L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                Assert.Contains(contract.Legs[0].Departure.Icao, origins);
                destinations.Add(contract.Legs[^1].Arrival.Icao);
            }
        }

        Assert.True(
            destinations.Except(origins).Count() > 10,
            "Jobs are barely reaching outside the airline's own network - the board would feel small.");
    }

    // ---------- Scale ----------

    /// <summary>
    /// <b>The scale is randomised, not just the endpoints.</b> The user asked for jobs that "could be
    /// massive or just small domestic flights", so a board has to carry a real spread - a
    /// forty-minute hop beside something that takes several sessions. A board where every job is the
    /// same size is the same failure as a generator that always offers four legs.
    /// </summary>
    [Fact]
    public void TheBoardCarriesAGenuineSpreadOfSizes()
    {
        var distances = new List<double>();
        var legCounts = new List<int>();

        for (var bucket = 20_000L; bucket < 20_030L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                distances.Add(contract.TotalDistanceNm);
                legCounts.Add(contract.Legs.Count);
            }
        }

        Assert.NotEmpty(distances);
        Assert.True(distances.Min() < 300, $"Nothing short on the board at all - shortest job was {distances.Min():F0} nm.");
        Assert.True(distances.Max() > 2_000, $"Nothing long on the board at all - longest job was {distances.Max():F0} nm.");
        Assert.True(legCounts.Contains(1), "No single-sector jobs were ever offered.");
        Assert.True(legCounts.Max() >= 3, $"No multi-leg job was ever offered - the longest had {legCounts.Max()} leg(s).");
    }

    /// <summary>
    /// How often a multi-leg expedition actually turns up, measured rather than assumed - because the
    /// multi-leg ferry is the headline of this whole feature and "it can happen" is not the same
    /// claim as "a player will see one".
    ///
    /// <para><b>The measured figures, over 200 boards of 8 jobs each:</b> about 19% of jobs are
    /// multi-leg, so a typical board carries one or two, and roughly one board in five carries none
    /// at all. Leg counts reach 10 - a Cessna 172 crossing an ocean - while a TBM 930 or an A320neo
    /// almost never chains, which is correct: they have the legs to go direct.</para>
    ///
    /// <para><b>This test pins the rate rather than merely proving it is non-zero.</b> The bounds are
    /// deliberately wide, because the exact figure is a balance decision that belongs in
    /// economy-config.json's scaleBands and to whoever is tuning it - but a change that quietly took
    /// multi-leg jobs from one-in-five to one-in-fifty would otherwise be invisible, and it would gut
    /// the most distinctive thing in the app without failing anything.</para>
    /// </summary>
    [Fact]
    public void MultiLegExpeditions_TurnUpOftenEnoughToBeAThingPlayersSee()
    {
        var total = 0;
        var multiLeg = 0;
        var longest = 0;

        for (var bucket = 20_000L; bucket < 20_200L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                total++;
                if (contract.Legs.Count > 1)
                {
                    multiLeg++;
                }

                longest = Math.Max(longest, contract.Legs.Count);
            }
        }

        var rate = (double)multiLeg / total;
        Assert.InRange(rate, 0.10, 0.45);
        Assert.True(longest >= 6, $"The longest expedition ever generated was {longest} legs - the epic shape has gone.");
    }

    /// <summary>All three kinds actually appear. Variety is the stated appeal, and one of three
    /// silently never being generated is exactly the failure that would be easy to miss.</summary>
    [Fact]
    public void AllThreeKindsOfJob_AreOffered()
    {
        var kinds = new HashSet<ContractKind>();

        for (var bucket = 20_000L; bucket < 20_020L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                kinds.Add(contract.Kind);
            }
        }

        Assert.Equal(3, kinds.Count);
    }

    /// <summary>A ferry carries nothing - the aeroplane is the cargo. Cargo carries freight and a
    /// charter carries people, and neither carries the other.</summary>
    [Fact]
    public void EachKindCarriesWhatItShould()
    {
        for (var bucket = 20_000L; bucket < 20_030L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                switch (contract.Kind)
                {
                    case ContractKind.Ferry:
                        Assert.Equal(0, contract.PayloadKg);
                        Assert.Equal(0, contract.PaxCount);
                        break;
                    case ContractKind.Cargo:
                        Assert.True(contract.PayloadKg > 0);
                        Assert.Equal(0, contract.PaxCount);
                        Assert.True(contract.PayloadKg <= contract.Aircraft.PayloadKg,
                            $"A {contract.Aircraft.TypeDesignator} was asked to carry {contract.PayloadKg} kg against a useful load of {contract.Aircraft.PayloadKg} kg.");
                        break;
                    case ContractKind.Charter:
                        Assert.True(contract.PaxCount > 0);
                        Assert.Equal(0, contract.PayloadKg);
                        Assert.True(contract.PaxCount <= contract.Aircraft.Seats,
                            $"A {contract.Aircraft.TypeDesignator} was given {contract.PaxCount} passengers for {contract.Aircraft.Seats} seats.");
                        break;
                }
            }
        }
    }

    // ---------- Money and dates ----------

    /// <summary>The per-leg shares always sum to exactly the fee the board advertises.</summary>
    [Fact]
    public void EveryJobsLegSharesSumToItsAdvertisedFee()
    {
        for (var bucket = 20_000L; bucket < 20_040L; bucket++)
        {
            foreach (var contract in ContractGenerator.Generate(Config, Request(bucket: bucket)).Contracts)
            {
                Assert.Equal(contract.Legs.Count, contract.FeeShares.Count);
                Assert.Equal(contract.Fee, contract.FeeShares.Sum());
            }
        }
    }

    /// <summary>
    /// The deadline is generous, fixed at generation, and the same for every job on a board - so it
    /// is known before the player accepts and cannot move afterwards. It never ambushes them.
    /// </summary>
    [Fact]
    public void EveryJobCarriesAGenerousDeadlineFixedAtGeneration()
    {
        var board = ContractGenerator.Generate(Config, Request());

        Assert.All(board.Contracts, c =>
        {
            Assert.Equal(Config.DeadlineDays, (c.DeadlineUtc - c.OfferedUtc).Days);
            Assert.True((c.DeadlineUtc - c.OfferedUtc) > TimeSpan.FromDays(14), "The deadline is not measured in weeks.");
        });
    }

    // ---------- Degrading honestly ----------

    /// <summary>
    /// <b>A thin board says so.</b> With one small aeroplane available, most slots cannot be filled -
    /// and the player must be told that the reason is their aircraft list, not that the feature is
    /// broken. Silently returning three jobs where there should be eight looks exactly like a bug.
    /// </summary>
    [Fact]
    public void WithAlmostNoAircraftAvailable_TheBoardIsThinAndSaysWhy()
    {
        var board = ContractGenerator.Generate(Config, Request(Aircraft("C152")));

        Assert.True(board.Contracts.Count < Config.BoardSize);
        Assert.Equal(1, board.Limitation.AvailableAircraftCount);
        Assert.Equal(Config.BoardSize, board.Limitation.Requested);
        Assert.Equal(board.Contracts.Count, board.Limitation.Generated);
        Assert.NotNull(board.Limitation.Message);
        Assert.Contains("Settings", board.Limitation.Message);

        // And what it does offer is still genuinely flyable - degrading honestly is not the same as
        // degrading into nonsense.
        Assert.All(board.Contracts, c => Assert.All(c.Legs, l =>
            Assert.True(l.DistanceNm <= RouteRangeAssessor.OperationalRangeNm(c.Aircraft.RangeNm))));
    }

    /// <summary>
    /// No aircraft ticked at all: an empty board, and a message that names the one place to fix it.
    /// Never an exception, and never a silently empty list.
    /// </summary>
    [Fact]
    public void WithNoAircraftAvailable_TheBoardIsEmptyAndExplainsItself()
    {
        var board = ContractGenerator.Generate(Config, Request(Array.Empty<ContractAircraft>()));

        Assert.Empty(board.Contracts);
        Assert.NotNull(board.Limitation.Message);
        Assert.Contains("Settings", board.Limitation.Message);
    }

    /// <summary>
    /// A brand-new airline that touches nowhere yet gets a different explanation, because it is a
    /// different problem with a different fix. Telling them to tick more aircraft would send them to
    /// a screen that cannot help.
    /// </summary>
    [Fact]
    public void WithNoAirportsTheAirlineTouches_TheMessageNamesThatInstead()
    {
        var board = ContractGenerator.Generate(Config, Request(origins: Array.Empty<ContractAirport>()));

        Assert.Empty(board.Contracts);
        Assert.NotNull(board.Limitation.Message);
        Assert.Contains("does not fly anywhere yet", board.Limitation.Message);
    }

    /// <summary>A full board reports no limitation at all - the message must be null, not an empty
    /// string, so a caller can tell "nothing to say" from "something to say, left blank".</summary>
    [Fact]
    public void AFullBoardHasNoLimitationMessage()
    {
        var full = Enumerable.Range(20_000, 200)
            .Select(b => ContractGenerator.Generate(Config, Request(bucket: b)))
            .FirstOrDefault(b => b.Contracts.Count == Config.BoardSize);

        Assert.NotNull(full);
        Assert.Null(full!.Limitation.Message);
    }
}

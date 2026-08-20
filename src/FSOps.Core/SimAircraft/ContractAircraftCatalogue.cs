using FSOps.Core.Flights;

namespace FSOps.Core.SimAircraft;

/// <summary>
/// Every aircraft a contract may be written for. Read the class doc on <see cref="ContractAircraft"/>
/// first: this list is deliberately NOT the fleet catalogue and the two must never be merged.
///
/// <para><b>How the edition tags were arrived at, because guessing here is the one failure that
/// matters.</b> A curated "ships with MSFS 2024" list assembled from memory is wrong by
/// construction - the three editions carry different aircraft and no two write-ups of the split
/// agree. So the tags below are anchored on two things that can be checked rather than recalled:
/// the published Deluxe and Premium Deluxe aircraft lists, and what is physically on the disk of a
/// known-Standard install. Those two agree exactly - every aircraft named as Deluxe or Premium
/// Deluxe is absent from a Standard install as a flyable package, and present only as a
/// <c>passiveaircraft-</c> AI model. Where the two did not agree, or where an aircraft appears in
/// neither, it is tagged <see cref="SimAircraftAvailability.AddOn"/>.</para>
///
/// <para><b>The bias is always downwards.</b> An aircraft wrongly tagged Standard hands somebody a
/// contract they cannot fly, which is the exact failure this feature exists to prevent. An aircraft
/// wrongly tagged AddOn just means the player ticks a box. So anything uncertain is an add-on.</para>
///
/// <para>The list is deliberately narrower than "every aircraft in the simulator": gliders,
/// balloons, helicopters, aerobatic mounts and warbirds are left out because no contract sensibly
/// asks for one. Adding them later costs nothing - nothing keys off the list being complete.</para>
/// </summary>
public static class ContractAircraftCatalogue
{
    /// <summary>
    /// Every entry, in a stable order (roughly smallest to largest within each category). Callers
    /// may rely on the order being stable but never on it being meaningful.
    /// </summary>
    public static IReadOnlyList<ContractAircraft> All { get; } = Build();

    private static readonly Dictionary<string, ContractAircraft> ByDesignator =
        All.ToDictionary(a => a.TypeDesignator, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks an aircraft up by ICAO type designator, case-insensitively. Tolerates the trailing
    /// junk real <c>aircraft.cfg</c> files carry - iniBuilds' A350 ULR variant writes
    /// <c>icao_type_designator = A359 ULR</c>, and a lookup that failed on that would silently miss
    /// an aircraft the player demonstrably owns.
    /// </summary>
    public static ContractAircraft? Find(string? typeDesignator)
    {
        var normalised = NormaliseDesignator(typeDesignator);
        if (normalised is null)
        {
            return null;
        }

        return ByDesignator.TryGetValue(normalised, out var aircraft) ? aircraft : null;
    }

    /// <summary>
    /// Trims a designator as read from a config file down to the bare code: first whitespace-
    /// delimited token, quotes stripped, upper-cased. Null for anything blank.
    /// </summary>
    public static string? NormaliseDesignator(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim().Trim('"').Trim();
        var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken) ? null : firstToken.ToUpperInvariant();
    }

    /// <summary>
    /// Falls back to the freeform TITLE / ATC MODEL text when a package does not declare an ICAO
    /// designator. Returns the first catalogue entry whose patterns match; null when nothing does,
    /// which is a normal answer and never an error - see <see cref="AircraftTypeMatcher"/>.
    /// </summary>
    public static ContractAircraft? FindByText(string? title, string? atcModel)
    {
        if (!AircraftTypeMatcher.HasAircraftData(title, atcModel))
        {
            return null;
        }

        return All.FirstOrDefault(a => AircraftTypeMatcher.IsMatch(a.MatchPatterns, title, atcModel));
    }

    /// <summary>Every base-content package id in the catalogue, mapped to the aircraft it delivers.</summary>
    public static IReadOnlyDictionary<string, ContractAircraft> ByBasePackageId { get; } =
        All.SelectMany(a => a.BasePackageIds.Select(id => (id, a)))
            .ToDictionary(pair => pair.id, pair => pair.a, StringComparer.OrdinalIgnoreCase);

    private static List<ContractAircraft> Build() =>
        new()
        {
            // ---- Light singles -------------------------------------------------------------
            new("C152", "Cessna 152", "Cessna", ContractAircraftCategory.LightSingle,
                Seats: 1, PayloadKg: 180, RangeNm: 415, CruiseTasKts: 100,
                """["C152","Cessna.{0,6}152"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-c152", "fs24-asobo-aircraft-c152-aerobat" }),

            new("C172", "Cessna 172 Skyhawk", "Cessna", ContractAircraftCategory.LightSingle,
                Seats: 3, PayloadKg: 340, RangeNm: 640, CruiseTasKts: 122,
                """["C172","Cessna.{0,6}172","Skyhawk"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-c172sp-as1000", "fs24-asobo-aircraft-c172sp-classic" }),

            new("DR40", "Robin DR400", "Robin", ContractAircraftCategory.LightSingle,
                Seats: 3, PayloadKg: 300, RangeNm: 750, CruiseTasKts: 130,
                """["DR40","DR-?400"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-dr400" }),

            new("DA40", "Diamond DA40", "Diamond", ContractAircraftCategory.LightSingle,
                Seats: 3, PayloadKg: 300, RangeNm: 940, CruiseTasKts: 154,
                """["DA40","DA-?40"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-da40-ng", "fs24-asobo-aircraft-da40-tdi" }),

            new("P28A", "Piper PA-28 Archer", "Piper", ContractAircraftCategory.LightSingle,
                Seats: 3, PayloadKg: 320, RangeNm: 520, CruiseTasKts: 128,
                """["P28[AB]","PA-?28","Archer","Dakota"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-archer", "fs24-microsoft-aircraft-pa28-236-dakota" }),

            new("BE36", "Beechcraft Bonanza G36", "Beechcraft", ContractAircraftCategory.LightSingle,
                Seats: 5, PayloadKg: 420, RangeNm: 920, CruiseTasKts: 176,
                """["BE36","B36T","Bonanza"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-bonanza-g36" }),

            new("SR22", "Cirrus SR22", "Cirrus", ContractAircraftCategory.LightSingle,
                Seats: 4, PayloadKg: 400, RangeNm: 1049, CruiseTasKts: 183,
                """["SR22","SR-?22"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-sr22" }),

            new("C185", "Cessna 185 Skywagon", "Cessna", ContractAircraftCategory.LightSingle,
                Seats: 5, PayloadKg: 450, RangeNm: 720, CruiseTasKts: 145,
                """["C185","Cessna.{0,6}185","Skywagon"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-c185f-skywagon" }),

            new("C400", "Cessna 400 Corvalis TT", "Cessna", ContractAircraftCategory.LightSingle,
                Seats: 3, PayloadKg: 350, RangeNm: 1250, CruiseTasKts: 235,
                """["C400","Corvalis","TTx"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-c400-corvalis" }),

            new("DHC2", "De Havilland DHC-2 Beaver", "De Havilland Canada", ContractAircraftCategory.LightSingle,
                Seats: 6, PayloadKg: 900, RangeNm: 400, CruiseTasKts: 130,
                """["DHC2","DHC-?2","Beaver"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-dhc2" }),

            // ---- Light twins ---------------------------------------------------------------
            new("BE58", "Beechcraft Baron G58", "Beechcraft", ContractAircraftCategory.LightTwin,
                Seats: 5, PayloadKg: 480, RangeNm: 1480, CruiseTasKts: 200,
                """["BE58","B58T","Baron"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-baron-g58" }),

            new("DA62", "Diamond DA62", "Diamond", ContractAircraftCategory.LightTwin,
                Seats: 6, PayloadKg: 500, RangeNm: 1283, CruiseTasKts: 190,
                """["DA62","DA-?62"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-da62" }),

            new("G21", "Grumman G-21 Goose", "Grumman", ContractAircraftCategory.LightTwin,
                Seats: 7, PayloadKg: 900, RangeNm: 640, CruiseTasKts: 170,
                """["G21","G-?21","Goose"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-g-21" }),

            new("C404", "Cessna 404 Titan", "Cessna", ContractAircraftCategory.LightTwin,
                Seats: 9, PayloadKg: 1000, RangeNm: 1200, CruiseTasKts: 180,
                """["C404","Titan"]""",
                SimAircraftAvailability.Deluxe,
                new[] { "fs24-microsoft-aircraft-c404" }),

            // ---- Utility turboprops --------------------------------------------------------
            new("TBM9", "Daher TBM 930", "Daher", ContractAircraftCategory.UtilityTurboprop,
                Seats: 5, PayloadKg: 640, RangeNm: 1730, CruiseTasKts: 252,
                """["TBM[79]","TBM ?9[34]0"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-tbm930", "fs24-workingtitle-aircraft-tbm930-enhanced" }),

            new("PC6T", "Pilatus PC-6 Turbo Porter", "Pilatus", ContractAircraftCategory.UtilityTurboprop,
                Seats: 9, PayloadKg: 1000, RangeNm: 460, CruiseTasKts: 125,
                """["PC6T","PC-?6","Porter"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-pilatus-pc6" }),

            new("PC12", "Pilatus PC-12 NGX", "Pilatus", ContractAircraftCategory.UtilityTurboprop,
                Seats: 8, PayloadKg: 1000, RangeNm: 1800, CruiseTasKts: 285,
                """["PC12","PC-?12"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-pc12-ngx" }),

            new("BE9L", "Beechcraft King Air C90 GTx", "Beechcraft", ContractAircraftCategory.UtilityTurboprop,
                Seats: 6, PayloadKg: 700, RangeNm: 1260, CruiseTasKts: 226,
                """["BE9L","C90","King ?Air ?C?90"]""",
                SimAircraftAvailability.PremiumDeluxe,
                new[] { "fs24-microsoft-aircraft-c90" }),

            new("C208", "Cessna 208B Grand Caravan EX", "Cessna", ContractAircraftCategory.UtilityTurboprop,
                Seats: 11, PayloadKg: 1500, RangeNm: 960, CruiseTasKts: 185,
                """["C208","208B","Caravan"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-208b-grand-caravan-ex" }),

            new("B350", "Beechcraft King Air 350i", "Beechcraft", ContractAircraftCategory.UtilityTurboprop,
                Seats: 9, PayloadKg: 1200, RangeNm: 1800, CruiseTasKts: 312,
                """["B350","BE20","King ?Air ?350"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-kingair350" }),

            new("DHC6", "De Havilland DHC-6 Twin Otter", "De Havilland Canada", ContractAircraftCategory.UtilityTurboprop,
                Seats: 19, PayloadKg: 1900, RangeNm: 700, CruiseTasKts: 160,
                """["DHC6","DHC-?6","Twin ?Otter"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-dhc6" }),

            new("C408", "Cessna 408 SkyCourier", "Cessna", ContractAircraftCategory.UtilityTurboprop,
                Seats: 19, PayloadKg: 2720, RangeNm: 900, CruiseTasKts: 210,
                """["C408","Sky ?Courier"]""",
                SimAircraftAvailability.Deluxe,
                new[] { "fs24-microsoft-aircraft-c408" }),

            // ---- Business jets -------------------------------------------------------------
            new("SF50", "Cirrus Vision Jet SF50", "Cirrus", ContractAircraftCategory.BusinessJet,
                Seats: 4, PayloadKg: 500, RangeNm: 1200, CruiseTasKts: 300,
                """["SF50","Vision ?Jet"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-sf50", "fs24-microsoft-aircraft-sf50" }),

            new("C25C", "Cessna Citation CJ4", "Cessna", ContractAircraftCategory.BusinessJet,
                Seats: 8, PayloadKg: 900, RangeNm: 2165, CruiseTasKts: 451,
                """["C25[ABC]","CJ4","Citation ?CJ"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-cj4" }),

            new("PC24", "Pilatus PC-24", "Pilatus", ContractAircraftCategory.BusinessJet,
                Seats: 8, PayloadKg: 1130, RangeNm: 2000, CruiseTasKts: 440,
                """["PC24","PC-?24"]""",
                SimAircraftAvailability.PremiumDeluxe,
                new[] { "fs24-microsoft-aircraft-pc24" }),

            new("C68A", "Cessna Citation Longitude", "Cessna", ContractAircraftCategory.BusinessJet,
                Seats: 10, PayloadKg: 1600, RangeNm: 3500, CruiseTasKts: 483,
                """["C68A","Longitude"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-longitude", "fs24-workingtitle-aircraft-longitude-enhanced" }),

            // ---- Regional airliners --------------------------------------------------------
            new("DC3", "Douglas DC-3", "Douglas", ContractAircraftCategory.RegionalAirliner,
                Seats: 28, PayloadKg: 2700, RangeNm: 1000, CruiseTasKts: 170,
                """["DC3","DC-?3"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-dc3" }),

            new("SF34", "Saab 340B", "Saab", ContractAircraftCategory.RegionalAirliner,
                Seats: 34, PayloadKg: 3400, RangeNm: 850, CruiseTasKts: 250,
                """["SF34","Saab ?340","340B"]""",
                SimAircraftAvailability.PremiumDeluxe,
                new[] { "fs24-microsoft-aircraft-s340" }),

            new("AT42", "ATR 42-600", "ATR", ContractAircraftCategory.RegionalAirliner,
                Seats: 48, PayloadKg: 5300, RangeNm: 716, CruiseTasKts: 300,
                """["AT4[2356]","ATR.{0,3}42"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("AT72", "ATR 72-600", "ATR", ContractAircraftCategory.RegionalAirliner,
                Seats: 70, PayloadKg: 7500, RangeNm: 825, CruiseTasKts: 300,
                """["AT7[2356]","ATR.{0,3}72"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("DH8D", "De Havilland Dash 8 Q400", "De Havilland Canada", ContractAircraftCategory.RegionalAirliner,
                Seats: 78, PayloadKg: 8600, RangeNm: 1100, CruiseTasKts: 360,
                """["DH8D","Q400","DHC-?8","Dash.{0,2}8"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("CRJ9", "Bombardier CRJ-900", "Bombardier", ContractAircraftCategory.RegionalAirliner,
                Seats: 86, PayloadKg: 10_000, RangeNm: 1550, CruiseTasKts: 430,
                """["CRJ9","CRJ.{0,3}900"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("E195", "Embraer E195", "Embraer", ContractAircraftCategory.RegionalAirliner,
                Seats: 124, PayloadKg: 13_000, RangeNm: 2450, CruiseTasKts: 430,
                """["E195","E295","E-?195"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            // ---- Narrowbodies --------------------------------------------------------------
            new("A20N", "Airbus A320neo", "Airbus", ContractAircraftCategory.Narrowbody,
                Seats: 180, PayloadKg: 20_500, RangeNm: 3500, CruiseTasKts: 450,
                """["A20N","A320\\s*-?neo"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-a320neo" }),

            new("A21N", "Airbus A321neo", "Airbus", ContractAircraftCategory.Narrowbody,
                Seats: 220, PayloadKg: 25_500, RangeNm: 4000, CruiseTasKts: 450,
                """["A21N","A321"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-a321" }),

            new("B38M", "Boeing 737 MAX 8", "Boeing", ContractAircraftCategory.Narrowbody,
                Seats: 178, PayloadKg: 20_800, RangeNm: 3550, CruiseTasKts: 453,
                """["B3[789]M","737 ?MAX","737MAX"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-b737max" }),

            new("A320", "Airbus A320", "Airbus", ContractAircraftCategory.Narrowbody,
                Seats: 180, PayloadKg: 19_000, RangeNm: 3300, CruiseTasKts: 447,
                """["A320(?!\\s*-?neo)","A32NX","A319","A318"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("B738", "Boeing 737-800", "Boeing", ContractAircraftCategory.Narrowbody,
                Seats: 189, PayloadKg: 20_500, RangeNm: 3115, CruiseTasKts: 453,
                """["B73[6-9]","737-?[678]00"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("B752", "Boeing 757-200", "Boeing", ContractAircraftCategory.Narrowbody,
                Seats: 200, PayloadKg: 25_500, RangeNm: 3915, CruiseTasKts: 459,
                """["B75[23]","757"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            // ---- Widebodies ----------------------------------------------------------------
            // The A400M is a military outsize freighter rather than an airliner. It sits here
            // because its payload and the kind of contract it suits are widebody-scale, and adding
            // a category for a single aircraft would say less than this comment does.
            new("A400", "Airbus A400M Atlas", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 0, PayloadKg: 37_000, RangeNm: 3450, CruiseTasKts: 422,
                """["A400M?"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-a400m" }),

            new("A310", "Airbus A310-300", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 220, PayloadKg: 33_000, RangeNm: 5150, CruiseTasKts: 459,
                """["A310","A30B"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-a310-300" }),

            new("A333", "Airbus A330-300", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 300, PayloadKg: 45_000, RangeNm: 6350, CruiseTasKts: 470,
                """["A33[023]","A330"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-microsoft-aircraft-a330" }),

            new("B78X", "Boeing 787-10 Dreamliner", "Boeing", ContractAircraftCategory.Widebody,
                Seats: 336, PayloadKg: 43_000, RangeNm: 6430, CruiseTasKts: 488,
                """["B78X","787-?10"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-b787-10" }),

            new("B748", "Boeing 747-8", "Boeing", ContractAircraftCategory.Widebody,
                Seats: 410, PayloadKg: 76_000, RangeNm: 8000, CruiseTasKts: 490,
                """["B74[78]","747-?8"]""",
                SimAircraftAvailability.Standard,
                new[] { "fs24-asobo-aircraft-b7478i" }),

            new("B763", "Boeing 767-300ER", "Boeing", ContractAircraftCategory.Widebody,
                Seats: 245, PayloadKg: 43_000, RangeNm: 5980, CruiseTasKts: 459,
                """["B76[234]","767"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("B789", "Boeing 787-9 Dreamliner", "Boeing", ContractAircraftCategory.Widebody,
                Seats: 296, PayloadKg: 53_000, RangeNm: 7635, CruiseTasKts: 488,
                """["B789","787-?9"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("A359", "Airbus A350-900", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 325, PayloadKg: 53_000, RangeNm: 8100, CruiseTasKts: 488,
                """["A359","A350-? ?900"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("A35K", "Airbus A350-1000", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 366, PayloadKg: 60_000, RangeNm: 8700, CruiseTasKts: 488,
                """["A35K","A350-? ?1000"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("B77W", "Boeing 777-300ER", "Boeing", ContractAircraftCategory.Widebody,
                Seats: 396, PayloadKg: 68_000, RangeNm: 7370, CruiseTasKts: 490,
                """["B77[LW]","777"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),

            new("A388", "Airbus A380-800", "Airbus", ContractAircraftCategory.Widebody,
                Seats: 525, PayloadKg: 84_000, RangeNm: 8000, CruiseTasKts: 488,
                """["A388","A380"]""",
                SimAircraftAvailability.AddOn,
                Array.Empty<string>()),
        };
}

using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.SimAircraft;

/// <summary>
/// The aircraft.cfg reader, exercised against the exact shapes real packages use. Every sample here
/// is copied from a file on a real MSFS 2024 install rather than invented, because the point of a
/// tolerant reader is that it survives what developers actually write.
/// </summary>
public class AircraftConfigReaderTests
{
    /// <summary>FlyByWire's A380X: quoted designator, localisation token for the model.</summary>
    [Fact]
    public void Parse_ReadsAQuotedDesignatorFromTheGeneralSection()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "; Copyright (c) 2023-2024 FlyByWire Simulations",
            "[GENERAL]",
            "atc_type = \"TT:ATCCOM.ATC_NAME AIRBUS.0.text\"",
            "atc_model = \"TT:ATCCOM.AC_MODEL A380.0.text\"",
            "icao_type_designator = \"A388\"",
            "icao_manufacturer = \"AIRBUS\"",
        });

        Assert.Equal("A388", config.TypeDesignator);
        Assert.Equal("TT:ATCCOM.AC_MODEL A380.0.text", config.AtcModel);
        Assert.False(config.IsAiTrafficOnly);
    }

    /// <summary>
    /// iniBuilds' A350 ULR preset: no space around the equals sign, no quotes, and a trailing word
    /// after the designator.
    /// </summary>
    [Fact]
    public void Parse_ReadsAnUnquotedDesignatorWithTrailingText()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "[GENERAL]",
            "atc_model = \"A350-900 ULR\"",
            "icao_type_designator =A359 ULR",
        });

        Assert.Equal("A359 ULR", config.TypeDesignator);
        Assert.Equal("A359", ContractAircraftCatalogue.NormaliseDesignator(config.TypeDesignator));
    }

    /// <summary>
    /// iniBuilds' A350 <c>common</c> config has every interesting key commented out. Reading a
    /// commented value would attach a designator to a fragment that does not define one.
    /// </summary>
    [Fact]
    public void Parse_IgnoresCommentedOutKeys()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "[GENERAL]",
            ";atc_type = \"A359\"",
            ";icao_type_designator =A359",
            "icao_manufacturer =\"Airbus\"",
        });

        Assert.Null(config.TypeDesignator);
        Assert.Null(config.AtcModel);
    }

    /// <summary>
    /// FSLTL's traffic base ships 2,551 of these and declares <c>content_type: "AIRCRAFT"</c>.
    /// Every variation is flagged as AI traffic and unselectable, and treating one as an owned
    /// aircraft would put a "Generic Quad Jet Airliner" in somebody's hangar.
    /// </summary>
    [Fact]
    public void Parse_FlagsAConfigWhoseEveryVariationIsAiTrafficOnly()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"ASOBO4J\"",
            "icao_model = \"Generic Quad Jet Airliner\"",
            "[FLTSIM.0]",
            "title = \"FSLTL A320 Generic\"",
            "isAirTraffic = 1 ; airtraffic flag for variations",
            "isUserSelectable = 0 ; flag off for non selectable planes",
            "[FLTSIM.1]",
            "title = \"FSLTL A320 Generic 2\"",
            "isAirTraffic = 1",
            "isUserSelectable = 0",
        });

        Assert.True(config.IsAiTrafficOnly);
    }

    /// <summary>
    /// One flyable variation is enough. An add-on that also ships AI liveries must not disappear
    /// because most of its entries are unselectable.
    /// </summary>
    [Fact]
    public void Parse_DoesNotFlagAConfigWithAtLeastOneSelectableVariation()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"A320\"",
            "[FLTSIM.0]",
            "title = \"Airline AI livery\"",
            "isAirTraffic = 1",
            "isUserSelectable = 0",
            "[FLTSIM.1]",
            "title = \"FenixA320 CFM SL\"",
        });

        Assert.False(config.IsAiTrafficOnly);
        Assert.Equal("Airline AI livery", config.Title);
    }

    /// <summary>
    /// MSFS 2024's modular format splits an aircraft across fragments, and the Fenix A320 declares
    /// its designator in an attachment config that has no [FLTSIM] section at all. A file with no
    /// variations is a fragment, not AI traffic - reading it as AI would lose the aircraft entirely.
    /// </summary>
    [Fact]
    public void Parse_DoesNotFlagAFragmentWithNoVariationsAtAll()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            "[GENERAL]",
            "atc_model = \"TT:ATCCOM.AC_MODEL A320.0.text\"",
            "icao_type_designator = \"A320\"",
        });

        Assert.False(config.IsAiTrafficOnly);
        Assert.Equal("A320", config.TypeDesignator);
    }

    [Fact]
    public void Parse_SurvivesRubbish()
    {
        var config = AircraftConfigReader.Parse(new[]
        {
            string.Empty,
            "not a section and not a key",
            "[",
            "= no key",
            "                 ",
        });

        Assert.Null(config.TypeDesignator);
        Assert.Null(config.AtcModel);
        Assert.Null(config.Title);
        Assert.False(config.IsAiTrafficOnly);
    }
}

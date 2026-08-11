namespace FSOps.Core.Entities;

public class Pilot
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsPlayer { get; set; }

    public decimal MonthlySalary { get; set; }

    public double HoursFlown { get; set; }

    public double SkillRating { get; set; } = 50;

    /// <summary>
    /// When this pilot last completed a flight (player or virtual) - the ONLY clock
    /// <see cref="FSOps.Core.Scheduling.PilotSkillCalculator"/>'s idle decay is ever measured
    /// against, deliberately never wall-clock time since the app was last opened. A pilot on a
    /// standing schedule keeps this fresh while the app is closed (the wall-clock catch-up model
    /// resolves their flights regardless), so decay only ever reaches a pilot deliberately left
    /// with no schedule - see docs/PLAN.md "Progression - reputation and pilot skill". Null for a
    /// pilot who has never flown a single sector.
    /// </summary>
    public DateTimeOffset? LastFlewUtc { get; set; }

    public PilotStatus Status { get; set; } = PilotStatus.Available;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}

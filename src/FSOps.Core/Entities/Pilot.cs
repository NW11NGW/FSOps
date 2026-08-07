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

    public PilotStatus Status { get; set; } = PilotStatus.Available;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}

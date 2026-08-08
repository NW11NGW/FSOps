using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class PilotScheduleConfiguration : IEntityTypeConfiguration<PilotSchedule>
{
    public void Configure(EntityTypeBuilder<PilotSchedule> builder)
    {
        builder.HasKey(s => s.Id);

        // One schedule per pilot - PilotEndpoints.SaveScheduleAsync relies on this to safely
        // "get or create" without a race producing two rows for the same pilot.
        builder.HasIndex(s => s.PilotId).IsUnique();
        builder.HasIndex(s => s.AirlineId);
        builder.HasQueryFilter(s => s.DeletedUtc == null);
    }
}

public class PilotScheduleEntryConfiguration : IEntityTypeConfiguration<PilotScheduleEntry>
{
    public void Configure(EntityTypeBuilder<PilotScheduleEntry> builder)
    {
        builder.HasKey(e => e.Id);

        // DayOfWeek is stored as a plain int (EF's default enum mapping - no HasConversion here),
        // deliberately NOT HasConversion<string>() - see PilotScheduleEntry's own doc comment for
        // why: the project has twice nearly shipped a bug from an EF-scaffolded default landing on
        // an enum stored via HasConversion<string>() (a silent "" default), which a plain int
        // column cannot produce - the worst a missing value can default to here is 0 (Sunday), a
        // perfectly valid day rather than an invalid string.
        builder.HasIndex(e => e.PilotScheduleId);
        builder.HasQueryFilter(e => e.DeletedUtc == null);
    }
}

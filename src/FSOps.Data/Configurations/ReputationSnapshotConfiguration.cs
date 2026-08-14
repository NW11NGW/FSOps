using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class ReputationSnapshotConfiguration : IEntityTypeConfiguration<ReputationSnapshot>
{
    public void Configure(EntityTypeBuilder<ReputationSnapshot> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.DateUtc).HasMaxLength(10).IsRequired();

        // Unique per airline per day - the structural guarantee behind "insert-only, written once".
        // EconomyClockService checks before inserting, but a catch-up pass racing itself, or two
        // passes overlapping, must not be able to produce a second row for the same day: the
        // database refuses it outright rather than relying on that check having won.
        builder.HasIndex(s => new { s.AirlineId, s.DateUtc }).IsUnique();

        // Insert-only - no DeletedUtc, so no soft-delete filter here (same as FlightEvent).
    }
}

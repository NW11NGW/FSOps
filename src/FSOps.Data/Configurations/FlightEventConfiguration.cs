using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class FlightEventConfiguration : IEntityTypeConfiguration<FlightEvent>
{
    public void Configure(EntityTypeBuilder<FlightEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>();
        builder.HasIndex(e => e.FlightId);
        // Append-only - no DeletedUtc, so no soft-delete filter here.
    }
}

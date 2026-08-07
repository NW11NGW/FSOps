using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class FleetAircraftConfiguration : IEntityTypeConfiguration<FleetAircraft>
{
    public void Configure(EntityTypeBuilder<FleetAircraft> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Ownership).HasConversion<string>();
        builder.Property(f => f.Status).HasConversion<string>();
        builder.HasIndex(f => f.AirlineId);
        builder.HasQueryFilter(f => f.DeletedUtc == null);
    }
}

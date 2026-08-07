using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class FlightConfiguration : IEntityTypeConfiguration<Flight>
{
    public void Configure(EntityTypeBuilder<Flight> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Status).HasConversion<string>();
        builder.Property(f => f.Revenue).HasPrecision(18, 2);
        builder.Property(f => f.TotalCost).HasPrecision(18, 2);
        builder.HasIndex(f => f.AirlineId);
        builder.HasQueryFilter(f => f.DeletedUtc == null);
    }
}

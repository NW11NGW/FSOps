using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class MaintenanceEventConfiguration : IEntityTypeConfiguration<MaintenanceEvent>
{
    public void Configure(EntityTypeBuilder<MaintenanceEvent> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasConversion<string>();
        builder.Property(m => m.Cost).HasPrecision(18, 2);
        builder.HasIndex(m => m.FleetAircraftId);
        builder.HasQueryFilter(m => m.DeletedUtc == null);
    }
}

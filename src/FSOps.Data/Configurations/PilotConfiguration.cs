using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class PilotConfiguration : IEntityTypeConfiguration<Pilot>
{
    public void Configure(EntityTypeBuilder<Pilot> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.MonthlySalary).HasPrecision(18, 2);
        builder.Property(p => p.Status).HasConversion<string>();
        builder.HasIndex(p => p.AirlineId);
        builder.HasQueryFilter(p => p.DeletedUtc == null);
    }
}

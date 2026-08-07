using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    public void Configure(EntityTypeBuilder<Lease> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.MonthlyRate).HasPrecision(18, 2);
        builder.HasIndex(l => l.AirlineId);
        builder.HasQueryFilter(l => l.DeletedUtc == null);
    }
}

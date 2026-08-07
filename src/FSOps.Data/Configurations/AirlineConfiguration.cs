using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class AirlineConfiguration : IEntityTypeConfiguration<Airline>
{
    public void Configure(EntityTypeBuilder<Airline> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.StrategyProfile).HasConversion<string>();
        builder.HasIndex(a => a.OwnerUserId);
        builder.HasQueryFilter(a => a.DeletedUtc == null);
    }
}

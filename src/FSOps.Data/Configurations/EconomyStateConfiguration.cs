using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class EconomyStateConfiguration : IEntityTypeConfiguration<EconomyState>
{
    public void Configure(EntityTypeBuilder<EconomyState> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FuelPricePerKg).HasPrecision(18, 4);
    }
}

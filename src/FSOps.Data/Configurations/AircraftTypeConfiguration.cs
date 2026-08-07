using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class AircraftTypeConfiguration : IEntityTypeConfiguration<AircraftType>
{
    public void Configure(EntityTypeBuilder<AircraftType> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.PurchasePrice).HasPrecision(18, 2);
        builder.Property(t => t.MonthlyLeaseRate).HasPrecision(18, 2);
        builder.HasIndex(t => t.IcaoType);
    }
}

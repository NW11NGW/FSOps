using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class AirportConfiguration : IEntityTypeConfiguration<Airport>
{
    public void Configure(EntityTypeBuilder<Airport> builder)
    {
        builder.HasKey(a => a.Icao);
        builder.Property(a => a.Icao).HasMaxLength(4).IsRequired();
        builder.Property(a => a.Iata).HasMaxLength(3);
        builder.Property(a => a.Name).IsRequired();
        builder.Property(a => a.SizeCategory).HasConversion<string>();

        builder.HasIndex(a => a.Icao);
        builder.HasIndex(a => a.Iata);
        builder.HasIndex(a => a.Name);

        builder.HasMany(a => a.Runways)
            .WithOne()
            .HasForeignKey(r => r.AirportIcao)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

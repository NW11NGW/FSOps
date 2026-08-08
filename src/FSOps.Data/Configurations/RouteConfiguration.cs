using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class RouteConfiguration : IEntityTypeConfiguration<Route>
{
    public void Configure(EntityTypeBuilder<Route> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.BaseFare).HasPrecision(18, 2);
        builder.Property(r => r.FlightNumber).HasMaxLength(5);
        builder.HasIndex(r => r.AirlineId);
        // Not unique at the DB level (nullable columns allow many NULLs in SQLite anyway) -
        // uniqueness of a supplied flight number is enforced in RouteEndpoints where a clearer
        // error message can be returned. This index just keeps the collision lookup fast.
        builder.HasIndex(r => new { r.AirlineId, r.FlightNumber });
        builder.HasQueryFilter(r => r.DeletedUtc == null);
    }
}

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
        builder.HasIndex(r => r.AirlineId);
        builder.HasQueryFilter(r => r.DeletedUtc == null);
    }
}

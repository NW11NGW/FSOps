using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class UserSettingsConfiguration : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.DistanceUnit).HasConversion<string>();
        builder.Property(s => s.AltitudeUnit).HasConversion<string>();
        builder.Property(s => s.WeightUnit).HasConversion<string>();
        builder.Property(s => s.TimeDisplay).HasConversion<string>();
        builder.HasIndex(s => s.OwnerUserId).IsUnique();
    }
}

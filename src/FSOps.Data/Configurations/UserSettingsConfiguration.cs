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

        // Stored as text like the other enums here, and given an explicit default so the column is
        // never NULL and never empty for a row written by anything other than EF. The migration that
        // adds it repeats this default for existing rows, because a string-converted enum with a
        // scaffolded default of "" fails to parse for every row already in the database - a mistake
        // this project has shipped before.
        builder.Property(s => s.UpdateChannel).HasConversion<string>().HasDefaultValue(UpdateChannel.Stable);

        builder.HasIndex(s => s.OwnerUserId).IsUnique();
    }
}

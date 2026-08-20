using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
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

        // Same treatment, same reason. Standard is the smallest aircraft set, so a row written
        // before editions existed lands on the answer that can only ever under-promise: the player
        // may have to tick a box, but they are never handed a contract for an aircraft they cannot
        // load. Declaring the default here is what stops EF scaffolding defaultValue: "" for it.
        builder.Property(s => s.SimEdition).HasConversion<string>().HasDefaultValue(SimEdition.Standard);

        builder.HasIndex(s => s.OwnerUserId).IsUnique();
    }
}

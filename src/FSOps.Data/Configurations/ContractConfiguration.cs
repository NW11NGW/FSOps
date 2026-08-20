using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.HasKey(c => c.Id);

        // Stored as TEXT, like every other enum in this app, so member order carries no meaning and
        // adding a kind or a status needs no migration.
        builder.Property(c => c.Kind).HasConversion<string>();
        builder.Property(c => c.Status).HasConversion<string>();

        builder.Property(c => c.Fee).HasPrecision(18, 2);
        builder.Property(c => c.CompletionBonus).HasPrecision(18, 2);

        builder.HasIndex(c => c.AirlineId);

        // The board is regenerated idempotently: a second read of the same period must find the rows
        // it already wrote rather than writing a near-identical second set. This is what makes that
        // lookup cheap, and the uniqueness is asserted rather than assumed.
        builder.HasIndex(c => new { c.AirlineId, c.BoardBucket, c.BoardSlot }).IsUnique();

        builder.HasMany(c => c.Legs)
            .WithOne()
            .HasForeignKey(l => l.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(c => c.DeletedUtc == null);
    }
}

public class ContractLegConfiguration : IEntityTypeConfiguration<ContractLeg>
{
    public void Configure(EntityTypeBuilder<ContractLeg> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.FeeShare).HasPrecision(18, 2);
        builder.HasIndex(l => l.ContractId);

        // A leg is only ever reached through its contract, so its filter has to agree with the
        // contract's own - EF requires a matching filter on the dependent side of a filtered
        // relationship, and without it a soft-deleted contract's legs would still be queryable.
        builder.HasQueryFilter(l => l.DeletedUtc == null);
    }
}

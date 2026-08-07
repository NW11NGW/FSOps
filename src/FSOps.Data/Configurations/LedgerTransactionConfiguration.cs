using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Category).HasConversion<string>();
        builder.Property(t => t.Amount).HasPrecision(18, 2);
        builder.HasIndex(t => t.AirlineId);
        // Append-only ledger - cash balance is SUM(Amount), never a mutable column.
    }
}

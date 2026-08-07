using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSOps.Data.Configurations;

public class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Principal).HasPrecision(18, 2);
        builder.Property(l => l.MonthlyPayment).HasPrecision(18, 2);
        builder.Property(l => l.RemainingBalance).HasPrecision(18, 2);
        builder.HasIndex(l => l.AirlineId);
        builder.HasQueryFilter(l => l.DeletedUtc == null);
    }
}

using Barbershop.Domain.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class ExpenseEntryConfiguration : IEntityTypeConfiguration<ExpenseEntry>
{
  public void Configure(EntityTypeBuilder<ExpenseEntry> builder)
  {
    builder.ToTable("expense_entries", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_expense_entries_amount_non_negative",
              "\"Amount\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
    builder.Property(x => x.OccurredOn).HasColumnType("date");
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<FixedExpense>()
        .WithMany()
        .HasForeignKey(x => x.FixedExpenseId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.OccurredOn);
  }
}

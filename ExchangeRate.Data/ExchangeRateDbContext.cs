using ExchangeRate.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace ExchangeRate.Data;

public class ExchangeRateDbContext : DbContext
{
    public ExchangeRateDbContext(DbContextOptions<ExchangeRateDbContext> options)
        : base(options)
    {
    }

    public DbSet<ExchangeRateRecord> ExchangeRates => Set<ExchangeRateRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExchangeRateRecord>(entity =>
        {
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.CurrencyCode);
            entity.Property(e => e.BuyRate).HasColumnType("decimal(18,4)");
            entity.Property(e => e.SellRate).HasColumnType("decimal(18,4)");
        });
    }
}

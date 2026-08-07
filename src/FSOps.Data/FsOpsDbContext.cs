using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Data;

public class FsOpsDbContext : DbContext
{
    public FsOpsDbContext(DbContextOptions<FsOpsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Airport> Airports => Set<Airport>();

    public DbSet<Runway> Runways => Set<Runway>();

    public DbSet<AircraftType> AircraftTypes => Set<AircraftType>();

    public DbSet<Airline> Airlines => Set<Airline>();

    public DbSet<FleetAircraft> FleetAircraft => Set<FleetAircraft>();

    public DbSet<Route> Routes => Set<Route>();

    public DbSet<Schedule> Schedules => Set<Schedule>();

    public DbSet<Pilot> Pilots => Set<Pilot>();

    public DbSet<Flight> Flights => Set<Flight>();

    public DbSet<FlightEvent> FlightEvents => Set<FlightEvent>();

    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();

    public DbSet<Loan> Loans => Set<Loan>();

    public DbSet<Lease> Leases => Set<Lease>();

    public DbSet<MaintenanceEvent> MaintenanceEvents => Set<MaintenanceEvent>();

    public DbSet<EconomyState> EconomyStates => Set<EconomyState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FsOpsDbContext).Assembly);
    }
}

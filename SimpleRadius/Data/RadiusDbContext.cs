using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;

namespace SimpleRadius.Data;

public class RadiusDbContext : DbContext
{
    public RadiusDbContext(DbContextOptions<RadiusDbContext> options) : base(options)
    {
    }

    public DbSet<VlanDefinition> VlanDefinitions => Set<VlanDefinition>();
    public DbSet<ClientDevice> ClientDevices => Set<ClientDevice>();
    public DbSet<NetworkAccessServer> NetworkAccessServers => Set<NetworkAccessServer>();
    public DbSet<AccountingSession> AccountingSessions => Set<AccountingSession>();
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();
    public DbSet<SsidVlanRule> SsidVlanRules => Set<SsidVlanRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerSettings>(entity =>
        {
            // A single row; the id is fixed rather than generated.
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.MacAddressFormat).HasConversion<string>();
        });

        modelBuilder.Entity<VlanDefinition>(entity =>
        {
            entity.HasIndex(v => v.Name).IsUnique();
            entity.HasIndex(v => v.VlanId).IsUnique();
        });

        modelBuilder.Entity<ClientDevice>(entity =>
        {
            entity.HasIndex(c => c.Name).IsUnique();

            // A client must always resolve to a VLAN, so a VLAN still in use cannot be deleted.
            entity.HasOne(c => c.VlanDefinition)
                .WithMany(v => v.Clients)
                .HasForeignKey(c => c.VlanDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NetworkAccessServer>(entity =>
        {
            entity.HasIndex(n => n.IpAddress).IsUnique();
        });

        modelBuilder.Entity<AccountingSession>(entity =>
        {
            // Acct-Session-Id is only unique within a NAS.
            entity.HasIndex(a => new { a.NasIpAddress, a.SessionId }).IsUnique();
            entity.HasIndex(a => a.IsActive);
            entity.HasIndex(a => a.StartTime);

            entity.HasOne(a => a.ClientDevice)
                .WithMany()
                .HasForeignKey(a => a.ClientDeviceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SsidVlanRule>(entity =>
        {
            entity.HasIndex(r => r.Ssid).IsUnique();

            // A rule must resolve to a VLAN, so a VLAN a rule points at cannot be deleted.
            entity.HasOne(r => r.VlanDefinition)
                .WithMany()
                .HasForeignKey(r => r.VlanDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }
}

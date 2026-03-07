using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetState.Shared.Models;

namespace NetState.Server.Data
{
    public class NetStateDbContext : DbContext
    {
        public DbSet<MonitoredDomain> Domains { get; set; } = null!;

        public NetStateDbContext(DbContextOptions<NetStateDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MonitoredDomain>()
                .HasKey(d => d.Id);
            
            modelBuilder.Entity<MonitoredDomain>()
                .Property(d => d.Name)
                .IsRequired();
                
            modelBuilder.Entity<MonitoredDomain>()
                .Property(d => d.Url)
                .IsRequired();

            modelBuilder.Entity<MonitoredDomain>()
                .Property(d => d.LastResponseHeaders)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(v, (JsonSerializerOptions?)null));

            modelBuilder.Entity<MonitoredDomain>()
                .Property(d => d.ExpectedHeaders)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(v, (JsonSerializerOptions?)null));

            base.OnModelCreating(modelBuilder);
        }
    }
}

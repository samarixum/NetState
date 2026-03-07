using System.ComponentModel.DataAnnotations;
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

            base.OnModelCreating(modelBuilder);
        }
    }
}

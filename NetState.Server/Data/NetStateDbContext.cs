using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NetState.Shared.Models;

namespace NetState.Server.Data;

/* :: :: NetStateDbContext :: START :: */

/// <summary>
/// Entity Framework Core database context for persisting monitored domains and DNS entries in SQLite.
/// </summary>
public class NetStateDbContext : DbContext {
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly ValueComparer<Dictionary<string, string>?> DictionaryComparer = new(
        (left, right) => DictionariesEqual(left, right),
        dict => ComputeDictionaryHashCode(dict),
        dict => dict == null ? null : new Dictionary<string, string>(dict, dict.Comparer)
    );

    /* :: :: Properties :: START :: */

    /// <summary>
    /// Gets or sets the table of monitored domains, endpoints, DNS entries, and zone audits.
    /// </summary>
    public DbSet<MonitoredDomain> Domains { get; set; } = null!;

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="NetStateDbContext"/> class.
    /// </summary>
    /// <param name="options">The database context options.</param>
    public NetStateDbContext(DbContextOptions<NetStateDbContext> options) : base(options) {
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Methods :: START :: */

    /// <summary>
    /// Configures entity mappings, value converters, and indexes for SQLite storage.
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<MonitoredDomain>()
            .HasKey(d => d.Id);

        modelBuilder.Entity<MonitoredDomain>()
            .Property(d => d.Name)
            .IsRequired();

        modelBuilder.Entity<MonitoredDomain>()
            .Property(d => d.Url)
            .IsRequired();

        modelBuilder.Entity<MonitoredDomain>()
            .HasIndex(d => d.MainHostname);

        modelBuilder.Entity<MonitoredDomain>()
            .Property(d => d.LastResponseHeaders)
            .HasConversion(
                v => SerializeDictionary(v),
                v => DeserializeDictionary(v, StringComparer.OrdinalIgnoreCase)
            )
            .Metadata.SetValueComparer(DictionaryComparer);

        modelBuilder.Entity<MonitoredDomain>()
            .Property(d => d.ExpectedHeaders)
            .HasConversion(
                v => SerializeDictionary(v),
                v => DeserializeDictionary(v, StringComparer.OrdinalIgnoreCase)
            )
            .Metadata.SetValueComparer(DictionaryComparer);

        modelBuilder.Entity<MonitoredDomain>()
            .Property(d => d.MiscDnsValues)
            .HasConversion(
                v => SerializeDictionary(v),
                v => DeserializeDictionary(v, StringComparer.OrdinalIgnoreCase)
            )
            .Metadata.SetValueComparer(DictionaryComparer);

        base.OnModelCreating(modelBuilder);
    }

    private static string? SerializeDictionary(Dictionary<string, string>? value) {
        if (value == null) {
            return null;
        }

        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static Dictionary<string, string>? DeserializeDictionary(string? json, StringComparer comparer) {
        if (string.IsNullOrWhiteSpace(json) || json == "null") {
            return null;
        }

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        if (parsed == null) {
            return null;
        }

        return new Dictionary<string, string>(parsed, comparer);
    }

    private static bool DictionariesEqual(Dictionary<string, string>? left, Dictionary<string, string>? right) {
        if (ReferenceEquals(left, right)) {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count) {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in left) {
            if (!right.TryGetValue(pair.Key, out string? otherValue) || !string.Equals(pair.Value, otherValue, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    private static int ComputeDictionaryHashCode(Dictionary<string, string>? dict) {
        if (dict == null) {
            return 0;
        }

        HashCode hash = new();
        foreach (KeyValuePair<string, string> pair in dict.OrderBy(p => p.Key, StringComparer.Ordinal)) {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /* :: :: Methods :: END :: */
}

/* :: :: NetStateDbContext :: END :: */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NetState.Shared.Core;
using NetState.Shared.Models;

namespace NetState.Server.Data;

/* :: :: DatabaseSchemaUpgrader :: START :: */

/// <summary>
/// Safely upgrades existing SQLite databases created by earlier versions of NetState without losing data.
/// </summary>
public static class DatabaseSchemaUpgrader {
    /* :: :: Methods :: START :: */

    /// <summary>
    /// Ensures the database exists, applies any missing columns to the Domains table, and backfills main hostnames.
    /// </summary>
    /// <param name="db">The database context to upgrade.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task UpgradeAsync(NetStateDbContext db, CancellationToken ct = default) {
        await db.Database.EnsureCreatedAsync(ct);

        DbConnection connection = db.Database.GetDbConnection();
        bool shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) {
            await connection.OpenAsync(ct);
        }

        try {
            HashSet<string> existingColumns = await GetTableColumnsAsync(connection, "Domains", ct);

            await EnsureColumnAsync(connection, existingColumns, "MainHostname", "TEXT NULL", ct);
            await EnsureColumnAsync(connection, existingColumns, "EntryKind", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnAsync(connection, existingColumns, "DnsRecordType", "TEXT NULL", ct);
            await EnsureColumnAsync(connection, existingColumns, "StrictDnsCheck", "INTEGER NOT NULL DEFAULT 1", ct);
            await EnsureColumnAsync(connection, existingColumns, "AllowDnsAbsent", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnAsync(connection, existingColumns, "MiscDnsValues", "TEXT NULL", ct);

            await ExecuteNonQueryAsync(
                connection,
                "CREATE INDEX IF NOT EXISTS \"IX_Domains_MainHostname\" ON \"Domains\" (\"MainHostname\");",
                ct
            );
        } finally {
            if (shouldClose) {
                await connection.CloseAsync();
            }
        }

        List<MonitoredDomain> domains = await db.Domains.ToListAsync(ct);
        bool updatedAny = false;

        foreach (MonitoredDomain domain in domains) {
            if (string.IsNullOrWhiteSpace(domain.MainHostname)) {
                domain.MainHostname = DomainGroupingHelper.ExtractMainHostname(domain.Url);
                updatedAny = true;
            }
        }

        if (updatedAny) {
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken ct
    ) {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            if (!reader.IsDBNull(1)) {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task EnsureColumnAsync(
        DbConnection connection,
        HashSet<string> existingColumns,
        string columnName,
        string columnDefinition,
        CancellationToken ct
    ) {
        if (existingColumns.Contains(columnName)) {
            return;
        }

        string sql = $"ALTER TABLE \"Domains\" ADD COLUMN \"{columnName}\" {columnDefinition};";
        await ExecuteNonQueryAsync(connection, sql, ct);
        existingColumns.Add(columnName);
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken ct) {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /* :: :: Methods :: END :: */
}

/* :: :: DatabaseSchemaUpgrader :: END :: */

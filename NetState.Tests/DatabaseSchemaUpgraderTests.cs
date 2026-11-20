using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetState.Server.Data;
using NetState.Shared.Models;

namespace NetState.Tests;

/* :: :: DatabaseSchemaUpgraderTests :: START :: */

/// <summary>
/// Integration tests verifying SQLite legacy schema auto-upgrade, data preservation, and MiscDnsValues round-trips.
/// </summary>
[TestClass]
public class DatabaseSchemaUpgraderTests {
    /* :: :: Test Methods :: START :: */

    /// <summary>
    /// Verifies that <see cref="DatabaseSchemaUpgrader.UpgradeAsync"/> upgrades legacy SQLite schemas non-destructively and persists MiscDnsValues.
    /// </summary>
    [TestMethod]
    public async Task UpgradeAsyncMigratesLegacyDatabasePreservingExistingRows() {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"netstate_test_{Guid.NewGuid():N}.db");
        try {
            DbContextOptions<NetStateDbContext> options = new DbContextOptionsBuilder<NetStateDbContext>()
                .UseSqlite($"Data Source={tempDbPath}")
                .Options;

            Guid yggId = Guid.Parse("5443F405-F42A-4F3C-B00D-0D30D8F4DC96");
            Guid animeId = Guid.Parse("DE53C2B0-BA11-4A0D-BDCB-384E78C48FA3");

            // Create legacy 10-column schema matching original NetState before DNS support
            await using (NetStateDbContext setupDb = new(options)) {
                DbConnection conn = setupDb.Database.GetDbConnection();
                await conn.OpenAsync();
                await using DbCommand cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE ""Domains"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Domains"" PRIMARY KEY,
                        ""Name"" TEXT NOT NULL,
                        ""Url"" TEXT NOT NULL,
                        ""Expectation"" INTEGER NOT NULL,
                        ""ExpectedValue"" TEXT NULL,
                        ""LastStatus"" INTEGER NOT NULL,
                        ""LastError"" TEXT NULL,
                        ""LastResponseBody"" TEXT NULL,
                        ""LastResponseHeaders"" TEXT NULL,
                        ""ExpectedHeaders"" TEXT NULL,
                        ""LastChecked"" TEXT NULL
                    );
                    INSERT INTO ""Domains"" (""Id"", ""Name"", ""Url"", ""Expectation"", ""ExpectedValue"", ""LastStatus"", ""ExpectedHeaders"")
                    VALUES ('5443F405-F42A-4F3C-B00D-0D30D8F4DC96', 'ygg', 'https://yggdrasil.au', 0, 'https://github.com/yggdrasil-au', 1, '{""Server"":""cloudflare""}');
                    INSERT INTO ""Domains"" (""Id"", ""Name"", ""Url"", ""Expectation"", ""ExpectedValue"", ""LastStatus"")
                    VALUES ('DE53C2B0-BA11-4A0D-BDCB-384E78C48FA3', 'anime-dimension.com', 'https://anime-dimension.com', 2, '200', 3);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            // Execute schema upgrader
            await using (NetStateDbContext upgradeDb = new(options)) {
                await DatabaseSchemaUpgrader.UpgradeAsync(upgradeDb);

                List<MonitoredDomain> loaded = await upgradeDb.Domains.AsNoTracking().ToListAsync();
                Assert.AreEqual(2, loaded.Count);

                MonitoredDomain ygg = loaded.Single(d => d.Id == yggId);
                Assert.AreEqual("yggdrasil.au", ygg.MainHostname);
                Assert.AreEqual(MonitoredEntryKind.Host, ygg.EntryKind);
                Assert.IsTrue(ygg.StrictDnsCheck);
                Assert.AreEqual("cloudflare", ygg.ExpectedHeaders?["Server"]);

                MonitoredDomain anime = loaded.Single(d => d.Id == animeId);
                Assert.AreEqual("anime-dimension.com", anime.MainHostname);

                // Persist a new DNS entry with MiscDnsValues and reload cleanly
                upgradeDb.Domains.Add(new MonitoredDomain {
                    Name = "TXT default._bimi.yggdrasil.au",
                    Url = "default._bimi.yggdrasil.au",
                    MainHostname = "yggdrasil.au",
                    EntryKind = MonitoredEntryKind.DnsEntry,
                    Expectation = ExpectationType.DnsRecord,
                    DnsRecordType = "TXT",
                    StrictDnsCheck = true,
                    ExpectedValue = "v=BIMI1; l=https://cdn.yggdrasil.au/hotlink-ok/tree/file.svg;",
                    MiscDnsValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                        ["ProxyStatus"] = "DNS only",
                        ["AssertContains"] = "v=BIMI1;"
                    }
                });
                await upgradeDb.SaveChangesAsync();
            }

            await using (NetStateDbContext verifyDb = new(options)) {
                MonitoredDomain? bimi = await verifyDb.Domains.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Url == "default._bimi.yggdrasil.au");
                Assert.IsNotNull(bimi);
                Assert.AreEqual("DNS only", bimi.MiscDnsValues?["ProxyStatus"]);
                Assert.AreEqual("v=BIMI1;", bimi.MiscDnsValues?["AssertContains"]);
                Assert.AreEqual(ExpectationType.DnsRecord, bimi.Expectation);
            }
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) {
                File.Delete(tempDbPath);
            }
        }
    }

    /* :: :: Test Methods :: END :: */
}

/* :: :: DatabaseSchemaUpgraderTests :: END :: */

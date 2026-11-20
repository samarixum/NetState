using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NetState.Server.Data;
using NetState.Server.Services;
using NetState.Shared.Core;
using NetState.Shared.Models;

Diagnostics.Initialize("Server");

try {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Services.AddOpenApi();

    builder.Services.AddDbContext<NetStateDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=netstate.db")
    );

    builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
    builder.Services.AddSingleton<DomainChecker>();
    builder.Services.AddHostedService<MonitoringBackgroundService>();

    WebApplication app = builder.Build();

    using (IServiceScope scope = app.Services.CreateScope()) {
        NetStateDbContext db = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
        await DatabaseSchemaUpgrader.UpgradeAsync(db);
    }

    if (app.Environment.IsDevelopment()) {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    /* :: :: API Endpoints :: START :: */

    app.MapGet("/api/domains", async (NetStateDbContext db, CancellationToken ct) =>
        await db.Domains.AsNoTracking().ToListAsync(ct)
    ).WithName("GetDomains");

    app.MapPost("/api/domains", async (NetStateDbContext db, MonitoredDomain domain, CancellationToken ct) => {
        if (string.IsNullOrWhiteSpace(domain.MainHostname)) {
            List<string> existingMains = await db.Domains
                .Where(d => !string.IsNullOrWhiteSpace(d.MainHostname))
                .Select(d => d.MainHostname!)
                .Distinct()
                .ToListAsync(ct);

            domain.MainHostname = DomainGroupingHelper.ExtractMainHostname(domain.Url, existingMains);
        } else {
            domain.MainHostname = DomainGroupingHelper.ExtractHost(domain.MainHostname);
        }

        if (domain.Expectation is ExpectationType.DnsRecord or ExpectationType.ZoneAudit) {
            domain.EntryKind = MonitoredEntryKind.DnsEntry;
        }

        db.Domains.Add(domain);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/domains/{domain.Id}", domain);
    }).WithName("CreateDomain");

    app.MapDelete("/api/domains/{id}", async (NetStateDbContext db, Guid id, CancellationToken ct) => {
        MonitoredDomain? domain = await db.Domains.FindAsync(new object[] { id }, ct);
        if (domain == null) {
            return Results.NotFound();
        }

        db.Domains.Remove(domain);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }).WithName("DeleteDomain");

    app.MapPost("/api/domains/{id}/check", async (NetStateDbContext db, Guid id, DomainChecker checker, CancellationToken ct) => {
        MonitoredDomain? domain = await db.Domains.FindAsync(new object[] { id }, ct);
        if (domain == null) {
            return Results.NotFound();
        }

        List<MonitoredDomain> snapshot = await db.Domains.AsNoTracking().ToListAsync(ct);
        await checker.CheckDomainAsync(domain, ct, snapshot);
        await db.SaveChangesAsync(ct);

        return Results.Ok(domain);
    }).WithName("CheckDomain");

    app.MapPost("/api/domains/check-all", async (NetStateDbContext db, DomainChecker checker, CancellationToken ct) => {
        List<MonitoredDomain> domains = await db.Domains.ToListAsync(ct);
        if (domains.Count > 0) {
            await Parallel.ForEachAsync(
                domains,
                new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
                async (domain, token) => {
                    await checker.CheckDomainAsync(domain, token, domains);
                }
            );
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(domains);
    }).WithName("CheckAllDomains");

    app.MapPost("/api/domains/check-group/{mainHostname}", async (
        NetStateDbContext db,
        string mainHostname,
        DomainChecker checker,
        CancellationToken ct
    ) => {
        string targetMain = DomainGroupingHelper.ExtractHost(mainHostname);
        List<MonitoredDomain> allDomains = await db.Domains.ToListAsync(ct);
        List<string> knownMains = allDomains
            .Select(d => d.MainHostname)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<MonitoredDomain> groupDomains = allDomains
            .Where(d => string.Equals(
                DomainGroupingHelper.ResolveMainHostname(d, knownMains),
                targetMain,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToList();

        if (groupDomains.Count == 0) {
            return Results.NotFound();
        }

        await Parallel.ForEachAsync(
            groupDomains,
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (domain, token) => {
                await checker.CheckDomainAsync(domain, token, allDomains);
            }
        );

        await db.SaveChangesAsync(ct);
        return Results.Ok(groupDomains);
    }).WithName("CheckDomainGroup");

    app.MapPut("/api/domains/{id}", async (NetStateDbContext db, Guid id, MonitoredDomain updatedDomain, CancellationToken ct) => {
        MonitoredDomain? domain = await db.Domains.FindAsync(new object[] { id }, ct);
        if (domain == null) {
            return Results.NotFound();
        }

        domain.Name = updatedDomain.Name;
        domain.Url = updatedDomain.Url;
        domain.MainHostname = string.IsNullOrWhiteSpace(updatedDomain.MainHostname)
            ? DomainGroupingHelper.ExtractMainHostname(updatedDomain.Url)
            : DomainGroupingHelper.ExtractHost(updatedDomain.MainHostname);
        domain.EntryKind = updatedDomain.EntryKind;
        domain.Expectation = updatedDomain.Expectation;
        domain.DnsRecordType = updatedDomain.DnsRecordType;
        domain.ExpectedValue = updatedDomain.ExpectedValue;
        domain.StrictDnsCheck = updatedDomain.StrictDnsCheck;
        domain.AllowDnsAbsent = updatedDomain.AllowDnsAbsent;
        domain.ExpectedHeaders = updatedDomain.ExpectedHeaders;
        domain.MiscDnsValues = updatedDomain.MiscDnsValues;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }).WithName("UpdateDomain");

    app.MapGet("/api/dns/discover/{domain}", async (string domain, DomainChecker checker, CancellationToken ct) => {
        ZoneDiscoveryResult result = await checker.DiscoverZoneAsync(domain, ct);
        return Results.Ok(result);
    }).WithName("DiscoverZone");

    /* :: :: API Endpoints :: END :: */

    app.Run();
} catch (Exception ex) {
    Diagnostics.Bug("Critical server error", ex);
} finally {
    Diagnostics.Close();
}

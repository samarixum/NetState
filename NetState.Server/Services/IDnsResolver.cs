using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NetState.Server.Services;

/* :: :: DNS Models & Interfaces :: START :: */

/// <summary>
/// Represents a single parsed DNS resource record returned from a DNS query.
/// </summary>
/// <param name="Name">The owner hostname of the record.</param>
/// <param name="Type">The DNS record type name (for example A, AAAA, CNAME, MX, TXT, SRV, NS, SOA, CAA, HTTPS, or TYPEn).</param>
/// <param name="Section">The DNS message section (Answer, Authority, Additional).</param>
/// <param name="Ttl">The record Time To Live in seconds.</param>
/// <param name="Value">The normalized human-readable RDATA value.</param>
/// <param name="RawData">The raw RDATA bytes when available.</param>
public sealed record DnsRecordEntry(
    string Name,
    string Type,
    string Section,
    uint Ttl,
    string Value,
    byte[] RawData
);

/// <summary>
/// Represents the complete outcome of a DNS query executed against Windows DNS or an RFC 1035 DNS server.
/// </summary>
/// <param name="QueryHost">The queried FQDN.</param>
/// <param name="QueryType">The normalized DNS record type string.</param>
/// <param name="ResolverUsed">Identifier of the resolver path or DNS server used.</param>
/// <param name="Success">True when the DNS query completed without transport or protocol errors (including NXDOMAIN/NO_RECORDS).</param>
/// <param name="StatusCode">Normalized status string: OK, NXDOMAIN, NO_RECORDS, or ERROR.</param>
/// <param name="Records">Matching Answer section resource records.</param>
/// <param name="CnameChain">Intermediate CNAME targets observed in the Answer section, if any.</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is false.</param>
public sealed record DnsQueryOutcome(
    string QueryHost,
    string QueryType,
    string ResolverUsed,
    bool Success,
    string StatusCode,
    List<DnsRecordEntry> Records,
    List<string> CnameChain,
    string? ErrorMessage = null
);

/// <summary>
/// Resolves live DNS records without stale caching for NetState monitoring and zone auditing.
/// </summary>
public interface IDnsResolver {
    /// <summary>
    /// Queries DNS records for the specified hostname and record type.
    /// </summary>
    /// <param name="host">The hostname or FQDN to query.</param>
    /// <param name="recordType">The DNS record type name (A, AAAA, CNAME, MX, TXT, SRV, NS, SOA, CAA, TYPEn, etc.).</param>
    /// <param name="dnsServer">Optional explicit DNS server IP (with optional :port) for RFC 1035 wire queries.</param>
    /// <param name="bypassCache">Whether local resolver cache should be bypassed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The structured DNS query outcome.</returns>
    Task<DnsQueryOutcome> QueryAsync(
        string host,
        string recordType,
        string? dnsServer = null,
        bool bypassCache = true,
        CancellationToken ct = default
    );
}

/* :: :: DNS Models & Interfaces :: END :: */

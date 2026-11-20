using System;
using System.Collections.Generic;

namespace NetState.Shared.Models;

/* :: :: Enums :: START :: */

/// <summary>
/// Defines the validation strategy used when checking a monitored entry.
/// </summary>
public enum ExpectationType {
    /// <summary>
    /// Expects an HTTP 3xx redirect response and optionally matches the target Location URL.
    /// </summary>
    Redirect = 0,

    /// <summary>
    /// Expects the normalized HTML body SHA-256 hash to match the expected value.
    /// </summary>
    HtmlHash = 1,

    /// <summary>
    /// Expects the HTTP response status code to match the expected integer code.
    /// </summary>
    HttpStatus = 2,

    /// <summary>
    /// Expects live DNS query results for a hostname and DNS record type to match expected records without unregistered extras.
    /// </summary>
    DnsRecord = 3,

    /// <summary>
    /// Audits a main hostname zone to verify no unregistered subdomains or DNS record types exist.
    /// </summary>
    ZoneAudit = 4
}

/// <summary>
/// Categorizes how a monitored entry is grouped under its main hostname in the UI.
/// </summary>
public enum MonitoredEntryKind {
    /// <summary>
    /// A web host, service endpoint, tunnel, worker, R2 bucket, or Access-protected host displayed in the hosts dropdown.
    /// </summary>
    Host = 0,

    /// <summary>
    /// A DNS record or zone audit entry displayed in the DNS entries section under the main hostname.
    /// </summary>
    DnsEntry = 1
}

/// <summary>
/// Represents the most recent health status of a monitored domain or DNS entry.
/// </summary>
public enum CheckStatus {
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Down = 3
}

/* :: :: Enums :: END :: */
// //
/* :: :: Models :: START :: */

/// <summary>
/// Represents a monitored host, HTTP endpoint, or DNS record stored in NetState.
/// </summary>
public class MonitoredDomain {
    /// <summary>
    /// Unique identifier for the monitored entry.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable display name for this entry.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Target URL (for HTTP checks) or hostname/FQDN (for DNS checks).
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Root/main hostname used to group related hosts and DNS entries (e.g., anime-dimension.com).
    /// </summary>
    public string? MainHostname { get; set; }

    /// <summary>
    /// Determines whether this entry appears in the hosts dropdown or DNS entries section under the main hostname.
    /// </summary>
    public MonitoredEntryKind EntryKind { get; set; } = MonitoredEntryKind.Host;

    /// <summary>
    /// Validation method applied to this entry.
    /// </summary>
    public ExpectationType Expectation { get; set; }

    /// <summary>
    /// DNS record type (e.g., A, AAAA, CNAME, MX, TXT, SRV, NS, CAA, SOA, HTTPS, or custom TYPEn) when checking DNS.
    /// </summary>
    public string? DnsRecordType { get; set; }

    /// <summary>
    /// Expected target redirect URL, HTML hash, HTTP status code, or newline-delimited DNS record values.
    /// </summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// When true on a DNS check, any additional unregistered record values returned for the queried type fail validation.
    /// </summary>
    public bool StrictDnsCheck { get; set; } = true;

    /// <summary>
    /// When true on a DNS check, validation expects zero records (NXDOMAIN or NO_RECORDS) and alerts if any record appears.
    /// </summary>
    public bool AllowDnsAbsent { get; set; }

    /// <summary>
    /// Most recent check outcome.
    /// </summary>
    public CheckStatus LastStatus { get; set; } = CheckStatus.Unknown;

    /// <summary>
    /// Diagnostic error message from the most recent failed or degraded check.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Last captured HTTP response body or formatted DNS inspection report.
    /// </summary>
    public string? LastResponseBody { get; set; }

    /// <summary>
    /// Last captured HTTP response headers or DNS response metadata.
    /// </summary>
    public Dictionary<string, string>? LastResponseHeaders { get; set; }

    /// <summary>
    /// Expected HTTP response headers to assert against live responses.
    /// </summary>
    public Dictionary<string, string>? ExpectedHeaders { get; set; }

    /// <summary>
    /// Miscellaneous DNS metadata and optional custom DNS assertions (e.g., ProxyStatus, TunnelName, AssertContains, IgnoreSoaSerial).
    /// </summary>
    public Dictionary<string, string>? MiscDnsValues { get; set; }

    /// <summary>
    /// UTC timestamp of the last check execution.
    /// </summary>
    public DateTime? LastChecked { get; set; }
}

/* :: :: Models :: END :: */

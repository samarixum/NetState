using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetState.Shared.Core;
using NetState.Shared.Models;

namespace NetState.Server.Services;

/* :: :: DomainChecker :: START :: */

/// <summary>
/// Executes HTTP, DNS record, and DNS zone audit health checks for <see cref="MonitoredDomain"/> entries.
/// </summary>
public partial class DomainChecker {
    private static readonly string[] AuditApexRecordTypes = {
        "A", "AAAA", "CNAME", "MX", "TXT", "NS", "CAA", "SRV"
    };

    private static readonly string[] AuditSubdomainCandidates = {
        "www", "api", "app", "auth", "admin", "blog", "cal", "cam", "cdn", "cdn1", "cdn2", "cf",
        "dash", "db", "dev", "dev0", "dev1", "docs", "email", "endpoint", "files", "ftp", "game",
        "git", "grafana", "ha", "home", "img", "login", "mail", "mailapi", "mc", "mcmap", "media",
        "monitor", "mta-sts", "mx", "nas", "netstate", "panel", "phpmyadmin", "pma", "plex", "portainer",
        "repo", "ruby", "rubydev", "shop", "smtp", "sql", "ssh", "ssh1", "ssh2", "sso", "staging",
        "status", "store", "test", "vnc", "vnc1", "vpn", "webmail", "wiki",
        "_dmarc", "_mailchannels", "_domainconnect", "_mta-sts", "_smtp._tls", "_atproto", "_discord", "default._bimi"
    };

    private readonly ILogger<DomainChecker> _logger;
    private readonly IDnsResolver _dnsResolver;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _mailClient;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainChecker"/> class with system DNS resolution.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public DomainChecker(ILogger<DomainChecker> logger) : this(logger, new SystemDnsResolver(), null, null) {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainChecker"/> class using dependency injection.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dnsResolver">The DNS resolver used for DNS record and zone audit checks.</param>
    [ActivatorUtilitiesConstructor]
    public DomainChecker(ILogger<DomainChecker> logger, IDnsResolver dnsResolver) : this(logger, dnsResolver, null, null) {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainChecker"/> class with explicit test dependencies.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dnsResolver">The DNS resolver used for DNS record and zone audit checks.</param>
    /// <param name="httpClient">Optional HTTP client for web/endpoint checks and crt.sh discovery.</param>
    /// <param name="mailClient">Optional HTTP client for MailChannels alert delivery.</param>
    public DomainChecker(
        ILogger<DomainChecker> logger,
        IDnsResolver dnsResolver,
        HttpClient? httpClient,
        HttpClient? mailClient
    ) {
        _logger = logger;
        _dnsResolver = dnsResolver;

        if (httpClient != null) {
            _httpClient = httpClient;
        } else {
            HttpClientHandler handler = new() { AllowAutoRedirect = false };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NetState-Monitor/1.0");
        }

        _mailClient = mailClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Public Check Methods :: START :: */

    /// <summary>
    /// Runs the configured health check against a single <see cref="MonitoredDomain"/> entry.
    /// </summary>
    /// <param name="domain">The domain or DNS entry to check and update with live results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="allDomainsSnapshot">Optional full snapshot of monitored entries used by <see cref="ExpectationType.ZoneAudit"/> and cross-record assertions.</param>
    /// <param name="suppressAlerts">When true, suppresses MailChannels alert emails (useful during initial bulk seeding).</param>
    public async Task CheckDomainAsync(
        MonitoredDomain domain,
        CancellationToken ct,
        IReadOnlyList<MonitoredDomain>? allDomainsSnapshot = null,
        bool suppressAlerts = false
    ) {
        CheckStatus previousStatus = domain.LastStatus;

        try {
            Diagnostics.Log($"Checking entry: {domain.Name} ({domain.Expectation}) at {domain.Url}");

            if (domain.Expectation == ExpectationType.DnsRecord) {
                await CheckDnsRecordAsync(domain, allDomainsSnapshot, ct);
            } else if (domain.Expectation == ExpectationType.ZoneAudit) {
                await CheckZoneAuditAsync(domain, allDomainsSnapshot, ct);
            } else {
                await CheckHttpEndpointAsync(domain, ct);
            }
        } catch (Exception ex) {
            domain.LastChecked = DateTime.UtcNow;
            domain.LastStatus = CheckStatus.Down;
            domain.LastError = $"Exception: {ex.Message}";
            domain.LastResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["ExceptionType"] = ex.GetType().Name,
                ["Error"] = ex.Message
            };
            domain.LastResponseBody = null;
            _logger.LogError(ex, "Error checking entry {DomainName}", domain.Name);
        }

        if (!suppressAlerts && previousStatus != domain.LastStatus) {
            if (domain.LastStatus == CheckStatus.Down) {
                await SendMailChannelsAlertAsync(domain, $"Entry went offline/failed validation. Error: {domain.LastError}", ct);
            } else if (domain.LastStatus == CheckStatus.Healthy && previousStatus == CheckStatus.Down) {
                await SendMailChannelsAlertAsync(domain, "Entry has recovered and is now healthy.", ct);
            }
        }
    }

    /// <summary>
    /// Runs candidate discovery (wildcards, common subdomains, live apex DNS records, and best-effort crt.sh logs) for a domain.
    /// </summary>
    /// <param name="domain">The base domain name to discover.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The discovered hostnames and DNS records.</returns>
    public async Task<ZoneDiscoveryResult> DiscoverZoneAsync(string domain, CancellationToken ct = default) {
        string mainHost = DomainGroupingHelper.ExtractMainHostname(domain);
        bool wildcard = false;

        DnsQueryOutcome wildcardOutcome = await _dnsResolver.QueryAsync(
            $"_netstate-wildcard-probe-94821.{mainHost}",
            "A",
            dnsServer: null,
            bypassCache: true,
            ct
        );
        if (wildcardOutcome.Success && wildcardOutcome.Records.Count > 0) {
            wildcard = true;
        }

        ConcurrentDictionary<string, List<DnsRecordEntry>> apexRecords = new(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            AuditApexRecordTypes,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (recordType, token) => {
                DnsQueryOutcome outcome = await _dnsResolver.QueryAsync(mainHost, recordType, dnsServer: null, bypassCache: true, token);
                if (outcome.Success && outcome.Records.Count > 0) {
                    apexRecords[recordType] = outcome.Records;
                }
            }
        );

        ConcurrentDictionary<string, List<string>> discoveredHosts = new(StringComparer.OrdinalIgnoreCase);
        if (!wildcard) {
            await Parallel.ForEachAsync(
                AuditSubdomainCandidates,
                new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
                async (sub, token) => {
                    string candidate = $"{sub}.{mainHost}";
                    DnsQueryOutcome queryA = await _dnsResolver.QueryAsync(candidate, "A", dnsServer: null, bypassCache: true, token);
                    if (queryA.Success && (queryA.Records.Count > 0 || queryA.CnameChain.Count > 0)) {
                        discoveredHosts[candidate] = queryA.Records.Select(r => $"{r.Type}={r.Value}").ToList();
                        return;
                    }

                    if (sub.StartsWith('_') || sub.Contains("._", StringComparison.Ordinal)) {
                        DnsQueryOutcome queryTxt = await _dnsResolver.QueryAsync(candidate, "TXT", dnsServer: null, bypassCache: true, token);
                        if (queryTxt.Success && queryTxt.Records.Count > 0) {
                            discoveredHosts[candidate] = queryTxt.Records.Select(r => $"TXT={r.Value}").ToList();
                        }
                    }
                }
            );
        }

        (string crtStatus, HashSet<string> crtHosts) = await TryFetchCrtShHostnamesAsync(mainHost, ct);
        foreach (string crtHost in crtHosts) {
            if (wildcard) {
                discoveredHosts.TryAdd(crtHost, new List<string> { "CT Log" });
            } else if (!discoveredHosts.ContainsKey(crtHost)) {
                DnsQueryOutcome probe = await _dnsResolver.QueryAsync(crtHost, "A", dnsServer: null, bypassCache: true, ct);
                if (probe.Success && probe.Records.Count > 0) {
                    discoveredHosts[crtHost] = probe.Records.Select(r => $"{r.Type}={r.Value}").ToList();
                }
            }
        }

        return new ZoneDiscoveryResult(
            MainHostname: mainHost,
            WildcardDetected: wildcard,
            CrtShStatus: crtStatus,
            ApexRecords: apexRecords.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
            DiscoveredSubdomains: discoveredHosts.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
        );
    }

    /* :: :: Public Check Methods :: END :: */
    // //
    /* :: :: HTTP Endpoint Validation :: START :: */

    private async Task CheckHttpEndpointAsync(MonitoredDomain domain, CancellationToken ct) {
        using HttpResponseMessage response = await _httpClient.GetAsync(domain.Url, ct);
        domain.LastChecked = DateTime.UtcNow;

        Dictionary<string, string> headersDictionary = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers) {
            headersDictionary[header.Key] = string.Join(", ", header.Value);
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers) {
            headersDictionary[header.Key] = string.Join(", ", header.Value);
        }

        headersDictionary["StatusCode"] = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        domain.LastResponseHeaders = headersDictionary;

        string htmlData = await response.Content.ReadAsStringAsync(ct);
        domain.LastResponseBody = htmlData;

        if (domain.ExpectedHeaders != null && domain.ExpectedHeaders.Count > 0) {
            foreach (KeyValuePair<string, string> expected in domain.ExpectedHeaders) {
                if (!headersDictionary.TryGetValue(expected.Key, out string? actualValue) ||
                    !actualValue.Contains(expected.Value, StringComparison.OrdinalIgnoreCase)) {
                    domain.LastStatus = CheckStatus.Down;
                    domain.LastError = $"Header mismatch. Expected {expected.Key}: {expected.Value}";
                    return;
                }
            }
        }

        switch (domain.Expectation) {
            case ExpectationType.Redirect: {
                if (response.StatusCode is HttpStatusCode.Redirect or
                    HttpStatusCode.MovedPermanently or
                    HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.SeeOther or
                    HttpStatusCode.PermanentRedirect) {
                    string? redirectUrl = response.Headers.Location?.ToString();
                    if (redirectUrl != null &&
                        (string.IsNullOrEmpty(domain.ExpectedValue) ||
                         redirectUrl.Contains(domain.ExpectedValue, StringComparison.OrdinalIgnoreCase))) {
                        domain.LastStatus = CheckStatus.Healthy;
                        domain.LastError = null;
                    } else {
                        domain.LastStatus = CheckStatus.Degraded;
                        domain.LastError = $"Expected redirect to {domain.ExpectedValue} but got {redirectUrl ?? "<none>"}";
                    }
                } else {
                    domain.LastStatus = CheckStatus.Down;
                    domain.LastError = $"Expected redirect, but got status code {(int)response.StatusCode} ({response.StatusCode})";
                }
                break;
            }
            case ExpectationType.HtmlHash: {
                string cleanHtml = await GetCleanHtmlAsync(domain.LastResponseBody ?? string.Empty);
                string hash = ComputeSha256Hash(cleanHtml);
                if (string.Equals(hash, domain.ExpectedValue, StringComparison.OrdinalIgnoreCase)) {
                    domain.LastStatus = CheckStatus.Healthy;
                    domain.LastError = null;
                } else {
                    domain.LastStatus = CheckStatus.Degraded;
                    domain.LastError = $"HTML hash mismatch. Expected: {domain.ExpectedValue}, Got: {hash}";
                }
                break;
            }
            case ExpectationType.HttpStatus: {
                string actualCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                List<string> allowedCodes = (domain.ExpectedValue ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                if (allowedCodes.Contains(actualCode, StringComparer.Ordinal)) {
                    domain.LastStatus = CheckStatus.Healthy;
                    domain.LastError = null;
                } else {
                    domain.LastStatus = CheckStatus.Down;
                    domain.LastError = $"Expected status {domain.ExpectedValue} but got {(int)response.StatusCode} ({response.StatusCode})";
                }
                break;
            }
        }
    }

    /* :: :: HTTP Endpoint Validation :: END :: */
    // //
    /* :: :: DNS Record Validation :: START :: */

    private async Task CheckDnsRecordAsync(
        MonitoredDomain domain,
        IReadOnlyList<MonitoredDomain>? allDomainsSnapshot,
        CancellationToken ct
    ) {
        string queryHost = DomainGroupingHelper.ExtractHost(domain.Url);
        string recordType = string.IsNullOrWhiteSpace(domain.DnsRecordType)
            ? "A"
            : domain.DnsRecordType.Trim().ToUpperInvariant();

        string? dnsServer = GetMiscValue(domain, "AssertDnsServer");
        DnsQueryOutcome outcome = await _dnsResolver.QueryAsync(queryHost, recordType, dnsServer, bypassCache: true, ct);
        domain.LastChecked = DateTime.UtcNow;

        Dictionary<string, string> metaHeaders = new(StringComparer.OrdinalIgnoreCase) {
            ["QueryHost"] = outcome.QueryHost,
            ["RecordType"] = outcome.QueryType,
            ["Resolver"] = outcome.ResolverUsed,
            ["DnsStatus"] = outcome.StatusCode,
            ["RecordCount"] = outcome.Records.Count.ToString(CultureInfo.InvariantCulture)
        };

        if (outcome.Records.Count > 0) {
            metaHeaders["MinTtl"] = outcome.Records.Min(r => r.Ttl).ToString(CultureInfo.InvariantCulture);
            metaHeaders["MaxTtl"] = outcome.Records.Max(r => r.Ttl).ToString(CultureInfo.InvariantCulture);
        }

        if (outcome.CnameChain.Count > 0) {
            metaHeaders["CnameChain"] = string.Join(" -> ", outcome.CnameChain);
        }

        if (domain.MiscDnsValues != null) {
            foreach (KeyValuePair<string, string> kvp in domain.MiscDnsValues) {
                metaHeaders[$"Misc:{kvp.Key}"] = kvp.Value;
            }
        }

        domain.LastResponseHeaders = metaHeaders;
        domain.LastResponseBody = FormatDnsOutcomeReport(domain, outcome);

        if (!outcome.Success) {
            domain.LastStatus = CheckStatus.Down;
            domain.LastError = outcome.ErrorMessage ?? $"DNS query failed for {queryHost} ({recordType})";
            return;
        }

        bool ignoreSoaSerial = string.Equals(GetMiscValue(domain, "IgnoreSoaSerial"), "true", StringComparison.OrdinalIgnoreCase);
        List<string> actualValues = outcome.Records
            .Select(r => NormalizeDnsValue(recordType, r.Value, ignoreSoaSerial))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (domain.AllowDnsAbsent) {
            if (actualValues.Count == 0) {
                domain.LastStatus = CheckStatus.Healthy;
                domain.LastError = null;
            } else {
                domain.LastStatus = CheckStatus.Down;
                domain.LastError = $"Expected {recordType} on {queryHost} to be absent, but found {actualValues.Count} record(s): {string.Join("; ", actualValues)}";
            }
            return;
        }

        List<string> expectedRawLines = SplitExpectedLines(domain.ExpectedValue);
        List<string> expectedValues = expectedRawLines
            .Select(line => NormalizeDnsValue(recordType, line, ignoreSoaSerial))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        List<string> failures = new();

        if (expectedValues.Count == 0) {
            if (actualValues.Count == 0) {
                failures.Add($"No {recordType} records returned for {queryHost} (status={outcome.StatusCode})");
            }
        } else {
            HashSet<string> actualSet = new(actualValues, StringComparer.Ordinal);
            HashSet<string> expectedSet = new(expectedValues, StringComparer.Ordinal);

            if (recordType == "SOA" && ignoreSoaSerial) {
                // Allow matching 3-field prefix `{primary} {rname} *` against 7-field live SOA if user supplied compact SOA
                List<string> missingSoa = expectedValues
                    .Where(exp => !actualValues.Any(act => SoaMatchesWithOptionalCompactPrefix(exp, act)))
                    .ToList();
                List<string> extraSoa = domain.StrictDnsCheck
                    ? actualValues.Where(act => !expectedValues.Any(exp => SoaMatchesWithOptionalCompactPrefix(exp, act))).ToList()
                    : new List<string>();

                if (missingSoa.Count > 0) {
                    failures.Add($"Missing expected SOA: {string.Join("; ", missingSoa)}");
                }
                if (extraSoa.Count > 0) {
                    failures.Add($"Unregistered SOA found: {string.Join("; ", extraSoa)}");
                }
            } else {
                List<string> missing = expectedSet.Where(e => !actualSet.Contains(e)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                List<string> extra = domain.StrictDnsCheck
                    ? actualSet.Where(a => !expectedSet.Contains(a)).OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();

                if (missing.Count > 0) {
                    failures.Add($"Missing expected {recordType}: {string.Join("; ", missing)}");
                }

                if (extra.Count > 0) {
                    failures.Add($"Unregistered {recordType} found: {string.Join("; ", extra)}");
                }
            }
        }

        // Misc DNS Assertions
        string? assertContains = GetMiscValue(domain, "AssertContains");
        if (!string.IsNullOrWhiteSpace(assertContains)) {
            bool caseSensitive = string.Equals(GetMiscValue(domain, "AssertCaseSensitive"), "true", StringComparison.OrdinalIgnoreCase);
            StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!outcome.Records.Any(r => r.Value.Contains(assertContains, cmp))) {
                failures.Add($"No {recordType} record contained '{assertContains}'");
            }
        }

        string? assertMaxTtl = GetMiscValue(domain, "AssertMaxTtl");
        if (!string.IsNullOrWhiteSpace(assertMaxTtl) && uint.TryParse(assertMaxTtl, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint maxTtl)) {
            List<uint> highTtls = outcome.Records.Where(r => r.Ttl > maxTtl).Select(r => r.Ttl).ToList();
            if (highTtls.Count > 0) {
                failures.Add($"TTL {highTtls.Max()}s exceeded AssertMaxTtl={maxTtl}s");
            }
        }

        string? assertNoOther = GetMiscValue(domain, "AssertNoOtherRecordTypes");
        if (!string.IsNullOrWhiteSpace(assertNoOther) && !string.Equals(assertNoOther, "false", StringComparison.OrdinalIgnoreCase)) {
            string[] typesToCheck = string.Equals(assertNoOther, "true", StringComparison.OrdinalIgnoreCase)
                ? AuditApexRecordTypes
                : assertNoOther.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.ToUpperInvariant())
                    .ToArray();

            HashSet<string> allowedTypesForHost = new(StringComparer.OrdinalIgnoreCase) { recordType };
            if (allDomainsSnapshot != null) {
                foreach (MonitoredDomain sibling in allDomainsSnapshot) {
                    if (sibling.Expectation == ExpectationType.DnsRecord &&
                        !sibling.AllowDnsAbsent &&
                        string.Equals(DomainGroupingHelper.ExtractHost(sibling.Url), queryHost, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(sibling.DnsRecordType)) {
                        allowedTypesForHost.Add(sibling.DnsRecordType.Trim().ToUpperInvariant());
                    }
                }
            }

            foreach (string probeType in typesToCheck) {
                if (allowedTypesForHost.Contains(probeType)) {
                    continue;
                }

                DnsQueryOutcome otherOutcome = await _dnsResolver.QueryAsync(queryHost, probeType, dnsServer, bypassCache: true, ct);
                if (otherOutcome.Success && otherOutcome.Records.Count > 0 && otherOutcome.CnameChain.Count == 0) {
                    failures.Add($"Unregistered {probeType} record(s) found on {queryHost}: {string.Join("; ", otherOutcome.Records.Select(r => r.Value))}");
                }
            }
        }

        if (failures.Count > 0) {
            domain.LastStatus = CheckStatus.Down;
            domain.LastError = string.Join(" | ", failures);
        } else {
            domain.LastStatus = CheckStatus.Healthy;
            domain.LastError = null;
        }
    }

    /* :: :: DNS Record Validation :: END :: */
    // //
    /* :: :: Zone Audit Validation :: START :: */

    private async Task CheckZoneAuditAsync(
        MonitoredDomain domain,
        IReadOnlyList<MonitoredDomain>? allDomainsSnapshot,
        CancellationToken ct
    ) {
        string mainHost = DomainGroupingHelper.ResolveMainHostname(domain);
        string? dnsServer = GetMiscValue(domain, "AssertDnsServer");
        IReadOnlyList<MonitoredDomain> snapshot = allDomainsSnapshot ?? new List<MonitoredDomain> { domain };

        List<MonitoredDomain> zoneEntries = snapshot
            .Where(d => string.Equals(DomainGroupingHelper.ResolveMainHostname(d, new[] { mainHost }), mainHost, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HashSet<string> registeredHosts = new(StringComparer.OrdinalIgnoreCase) { mainHost };
        HashSet<(string Host, string Type)> registeredDnsTypes = new();

        foreach (MonitoredDomain entry in zoneEntries) {
            string entryHost = DomainGroupingHelper.ExtractHost(entry.Url);
            if (!string.IsNullOrEmpty(entryHost)) {
                registeredHosts.Add(entryHost);
            }

            if (entry.Expectation == ExpectationType.DnsRecord && !entry.AllowDnsAbsent && !string.IsNullOrWhiteSpace(entry.DnsRecordType)) {
                registeredDnsTypes.Add((entryHost, entry.DnsRecordType.Trim().ToUpperInvariant()));
            }
        }

        string? ignoreListRaw = GetMiscValue(domain, "IgnoreSubdomains");
        if (!string.IsNullOrWhiteSpace(ignoreListRaw)) {
            foreach (string ignored in ignoreListRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                registeredHosts.Add(DomainGroupingHelper.NormalizeDnsName(ignored));
            }
        }

        List<string> findings = new();
        List<string> resolverErrors = new();

        // 1. Wildcard check
        bool wildcardDetected = false;
        DnsQueryOutcome wildcardProbe = await _dnsResolver.QueryAsync($"_netstate-wildcard-audit-check.{mainHost}", "A", dnsServer, bypassCache: true, ct);
        if (!wildcardProbe.Success) {
            resolverErrors.Add($"Wildcard probe error: {wildcardProbe.ErrorMessage}");
        } else if (wildcardProbe.Records.Count > 0) {
            wildcardDetected = true;
            if (string.Equals(GetMiscValue(domain, "AssertNotWildcard"), "true", StringComparison.OrdinalIgnoreCase)) {
                findings.Add($"Wildcard DNS detected on *.{mainHost}: {string.Join(", ", wildcardProbe.Records.Select(r => r.Value))}");
            }
        }

        // 2. Apex DNS record type audit (ensure no unexpected A/AAAA/CNAME/MX/TXT/NS/CAA/SRV exist on mainHost)
        ConcurrentDictionary<string, DnsQueryOutcome> apexResults = new(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            AuditApexRecordTypes,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (type, token) => {
                DnsQueryOutcome outcome = await _dnsResolver.QueryAsync(mainHost, type, dnsServer, bypassCache: true, token);
                apexResults[type] = outcome;
            }
        );

        foreach (string type in AuditApexRecordTypes) {
            if (!apexResults.TryGetValue(type, out DnsQueryOutcome? outcome)) {
                continue;
            }

            if (!outcome.Success) {
                resolverErrors.Add($"Apex {type}: {outcome.ErrorMessage}");
                continue;
            }

            if (outcome.Records.Count > 0 && !registeredDnsTypes.Contains((mainHost, type))) {
                findings.Add($"Unregistered {type} record(s) on {mainHost}: {string.Join("; ", outcome.Records.Select(r => r.Value))}");
            }
        }

        // 3. Common & crt.sh subdomain discovery (only non-wildcard domains flag resolving subdomains)
        bool auditSubdomains = !string.Equals(GetMiscValue(domain, "AuditSubdomains"), "false", StringComparison.OrdinalIgnoreCase);
        ConcurrentBag<string> unregisteredSubdomains = new();
        string crtStatus = "NotQueried";

        if (auditSubdomains && !wildcardDetected) {
            HashSet<string> candidateFqdns = AuditSubdomainCandidates
                .Select(sub => $"{sub}.{mainHost}")
                .Where(fqdn => !registeredHosts.Contains(fqdn))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool enableCrtSh = string.Equals(GetMiscValue(domain, "EnableCrtSh"), "true", StringComparison.OrdinalIgnoreCase);
            if (enableCrtSh) {
                (string status, HashSet<string> crtHosts) = await TryFetchCrtShHostnamesAsync(mainHost, ct);
                crtStatus = status;
                foreach (string h in crtHosts) {
                    if (!registeredHosts.Contains(h)) {
                        candidateFqdns.Add(h);
                    }
                }
            } else {
                crtStatus = "Skipped (disabled for fast audit; enable with EnableCrtSh=true)";
            }

            await Parallel.ForEachAsync(
                candidateFqdns,
                new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
                async (candidate, token) => {
                    DnsQueryOutcome queryA = await _dnsResolver.QueryAsync(candidate, "A", dnsServer, bypassCache: true, token);
                    if (!queryA.Success) {
                        return;
                    }
                    if (queryA.Records.Count > 0 || queryA.CnameChain.Count > 0) {
                        string summary = queryA.Records.Count > 0
                            ? $"A={string.Join(",", queryA.Records.Select(r => r.Value))}"
                            : $"CNAME={string.Join("->", queryA.CnameChain)}";
                        unregisteredSubdomains.Add($"{candidate} ({summary})");
                        return;
                    }

                    DnsQueryOutcome queryAaaa = await _dnsResolver.QueryAsync(candidate, "AAAA", dnsServer, bypassCache: true, token);
                    if (queryAaaa.Success && queryAaaa.Records.Count > 0) {
                        unregisteredSubdomains.Add($"{candidate} (AAAA={string.Join(",", queryAaaa.Records.Select(r => r.Value))})");
                        return;
                    }

                    if (candidate.StartsWith('_') || candidate.Contains("._", StringComparison.Ordinal)) {
                        DnsQueryOutcome queryTxt = await _dnsResolver.QueryAsync(candidate, "TXT", dnsServer, bypassCache: true, token);
                        if (queryTxt.Success && queryTxt.Records.Count > 0) {
                            unregisteredSubdomains.Add($"{candidate} (TXT={string.Join(";", queryTxt.Records.Select(r => r.Value))})");
                        }
                    }
                }
            );

            foreach (string extraSub in unregisteredSubdomains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
                findings.Add($"Unregistered resolving subdomain: {extraSub}");
            }
        }

        // 4. Registered DNS hosts: ensure no unexpected direct A/AAAA/CNAME appeared on DNS-only hosts or vice versa
        foreach (string regHost in registeredHosts.Where(h => !h.Equals(mainHost, StringComparison.OrdinalIgnoreCase))) {
            bool hasRegisteredCname = registeredDnsTypes.Contains((regHost, "CNAME"));
            if (hasRegisteredCname) {
                continue;
            }

            // If host has NO A, AAAA, CNAME, HTTP, or TXT entry registered at all, skip;
            // If host ONLY has TXT registered (e.g. _dmarc), verify no rogue A/CNAME was added
            bool hasHttp = zoneEntries.Any(e => e.EntryKind == MonitoredEntryKind.Host &&
                                                string.Equals(DomainGroupingHelper.ExtractHost(e.Url), regHost, StringComparison.OrdinalIgnoreCase));
            bool hasAddrDns = registeredDnsTypes.Contains((regHost, "A")) || registeredDnsTypes.Contains((regHost, "AAAA"));
            if (!hasHttp && !hasAddrDns && registeredDnsTypes.Any(k => k.Host.Equals(regHost, StringComparison.OrdinalIgnoreCase))) {
                DnsQueryOutcome rogueA = await _dnsResolver.QueryAsync(regHost, "A", dnsServer, bypassCache: true, ct);
                if (rogueA.Success && rogueA.Records.Count > 0) {
                    findings.Add($"Unexpected A record on DNS-only host {regHost}: {string.Join(", ", rogueA.Records.Select(r => r.Value))}");
                }

                DnsQueryOutcome rogueCname = await _dnsResolver.QueryAsync(regHost, "CNAME", dnsServer, bypassCache: true, ct);
                if (rogueCname.Success && rogueCname.Records.Count > 0) {
                    findings.Add($"Unexpected CNAME record on DNS-only host {regHost}: {string.Join(", ", rogueCname.Records.Select(r => r.Value))}");
                }
            }
        }

        domain.LastChecked = DateTime.UtcNow;

        Dictionary<string, string> auditHeaders = new(StringComparer.OrdinalIgnoreCase) {
            ["MainHostname"] = mainHost,
            ["RegisteredHostsCount"] = registeredHosts.Count.ToString(CultureInfo.InvariantCulture),
            ["RegisteredDnsTypePairs"] = registeredDnsTypes.Count.ToString(CultureInfo.InvariantCulture),
            ["WildcardDns"] = wildcardDetected.ToString(),
            ["CrtShStatus"] = crtStatus,
            ["UnregisteredFindingsCount"] = findings.Count.ToString(CultureInfo.InvariantCulture),
            ["ResolverErrorsCount"] = resolverErrors.Count.ToString(CultureInfo.InvariantCulture)
        };
        domain.LastResponseHeaders = auditHeaders;

        StringBuilder report = new();
        report.AppendLine($"Zone Audit: {mainHost}");
        report.AppendLine($"Checked At (UTC): {domain.LastChecked:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Wildcard DNS: {wildcardDetected}");
        report.AppendLine($"Registered Hosts: {registeredHosts.Count}");
        report.AppendLine($"Registered Active DNS (Host, Type) Pairs: {registeredDnsTypes.Count}");
        report.AppendLine($"crt.sh Status: {crtStatus}");
        report.AppendLine();
        report.AppendLine("Apex Live DNS Summary:");
        foreach (string type in AuditApexRecordTypes) {
            if (apexResults.TryGetValue(type, out DnsQueryOutcome? outcome)) {
                string line = outcome.Records.Count > 0
                    ? string.Join(" | ", outcome.Records.Select(r => r.Value))
                    : $"<{outcome.StatusCode}>";
                report.AppendLine($"  {type,-5}: {line}");
            }
        }
        report.AppendLine();
        report.AppendLine($"Findings ({findings.Count}):");
        if (findings.Count == 0) {
            report.AppendLine("  None - all live records and subdomains match registered entries.");
        } else {
            foreach (string finding in findings) {
                report.AppendLine($"  - {finding}");
            }
        }
        if (resolverErrors.Count > 0) {
            report.AppendLine();
            report.AppendLine($"Resolver Errors ({resolverErrors.Count}):");
            foreach (string err in resolverErrors) {
                report.AppendLine($"  - {err}");
            }
        }
        domain.LastResponseBody = report.ToString();

        if (findings.Count > 0) {
            domain.LastStatus = CheckStatus.Down;
            domain.LastError = string.Join(" | ", findings);
        } else if (resolverErrors.Count > 0) {
            domain.LastStatus = CheckStatus.Degraded;
            domain.LastError = string.Join(" | ", resolverErrors);
        } else {
            domain.LastStatus = CheckStatus.Healthy;
            domain.LastError = null;
        }
    }

    private async Task<(string Status, HashSet<string> Hosts)> TryFetchCrtShHostnamesAsync(string mainHost, CancellationToken ct) {
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
        try {
            using CancellationTokenSource crtCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            crtCts.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage resp = await _httpClient.GetAsync($"https://crt.sh/?q=%.{Uri.EscapeDataString(mainHost)}&output=json", crtCts.Token);
            if (!resp.IsSuccessStatusCode) {
                return ($"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})", hosts);
            }

            string json = await resp.Content.ReadAsStringAsync(crtCts.Token);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) {
                return ("Unexpected JSON", hosts);
            }

            foreach (JsonElement elem in doc.RootElement.EnumerateArray()) {
                if (!elem.TryGetProperty("name_value", out JsonElement nv) || nv.ValueKind != JsonValueKind.String) {
                    continue;
                }

                foreach (string rawLine in (nv.GetString() ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                    string candidate = DomainGroupingHelper.NormalizeDnsName(rawLine);
                    if (!string.IsNullOrEmpty(candidate) &&
                        !candidate.Contains('*') &&
                        (candidate.Equals(mainHost, StringComparison.OrdinalIgnoreCase) ||
                         candidate.EndsWith($".{mainHost}", StringComparison.OrdinalIgnoreCase))) {
                        hosts.Add(candidate);
                    }
                }
            }

            return ($"OK ({hosts.Count} names)", hosts);
        } catch (Exception ex) {
            return ($"Skipped ({ex.Message})", hosts);
        }
    }

    /* :: :: Zone Audit Validation :: END :: */
    // //
    /* :: :: DNS Value Normalization Helpers :: START :: */

    internal static string NormalizeDnsValue(string recordType, string rawValue, bool ignoreSoaSerial = false) {
        string clean = (rawValue ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clean)) {
            return string.Empty;
        }

        switch (recordType.Trim().ToUpperInvariant()) {
            case "A":
            case "AAAA": {
                if (IPAddress.TryParse(clean, out IPAddress? ip)) {
                    return ip.ToString().ToLowerInvariant();
                }
                return clean.ToLowerInvariant();
            }
            case "CNAME":
            case "NS":
            case "PTR":
            case "DNAME": {
                return DomainGroupingHelper.NormalizeDnsName(clean);
            }
            case "MX": {
                string[] parts = WhitespaceSplitRegex().Split(clean);
                if (parts.Length >= 2 && ushort.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort pref)) {
                    string exchange = DomainGroupingHelper.NormalizeDnsName(parts[1]);
                    return $"{pref} {exchange}";
                }
                return clean.ToLowerInvariant();
            }
            case "SRV": {
                string[] parts = WhitespaceSplitRegex().Split(clean);
                if (parts.Length >= 4 &&
                    ushort.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort prio) &&
                    ushort.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort weight) &&
                    ushort.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port)) {
                    string target = DomainGroupingHelper.NormalizeDnsName(parts[3]);
                    return $"{prio} {weight} {port} {target}";
                }
                return clean.ToLowerInvariant();
            }
            case "TXT":
            case "SPF": {
                return NormalizeTxtValue(clean);
            }
            case "CAA": {
                Match match = CaaRegex().Match(clean);
                if (match.Success) {
                    string flags = match.Groups["flags"].Value;
                    string tag = match.Groups["tag"].Value.ToLowerInvariant();
                    string val = match.Groups["val"].Value.Trim();
                    if (val.Length >= 2 && val.StartsWith('"') && val.EndsWith('"')) {
                        val = val[1..^1];
                    }
                    return $"{flags} {tag} \"{val}\"";
                }
                return clean;
            }
            case "SOA": {
                string[] parts = WhitespaceSplitRegex().Split(clean);
                if (parts.Length >= 2) {
                    parts[0] = DomainGroupingHelper.NormalizeDnsName(parts[0]);
                    parts[1] = DomainGroupingHelper.NormalizeDnsName(parts[1]);
                }
                if (ignoreSoaSerial && parts.Length >= 3) {
                    parts[2] = "*";
                }
                return string.Join(' ', parts);
            }
            default: {
                return clean;
            }
        }
    }

    private static bool SoaMatchesWithOptionalCompactPrefix(string normalizedExpected, string normalizedActual) {
        if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal)) {
            return true;
        }

        string[] expParts = normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] actParts = normalizedActual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expParts.Length < 2 || actParts.Length < expParts.Length) {
            return false;
        }

        for (int i = 0; i < expParts.Length; i++) {
            if (expParts[i] == "*" && i == 2) {
                continue;
            }
            if (!string.Equals(expParts[i], actParts[i], StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeTxtValue(string raw) {
        string trimmed = raw.Trim();
        if (!trimmed.StartsWith('"')) {
            return trimmed;
        }

        // Parse RFC 1035 quoted character-string segments like "\"part1\" \"part2\""
        StringBuilder sb = new();
        bool inQuotes = false;
        bool parsedAnyQuote = false;
        for (int i = 0; i < trimmed.Length; i++) {
            char c = trimmed[i];
            if (!inQuotes) {
                if (c == '"') {
                    inQuotes = true;
                    parsedAnyQuote = true;
                } else if (!char.IsWhiteSpace(c)) {
                    // Fallback: mixed unquoted format
                    return trimmed;
                }
            } else {
                if (c == '\\' && i + 1 < trimmed.Length) {
                    sb.Append(trimmed[++i]);
                } else if (c == '"') {
                    inQuotes = false;
                } else {
                    sb.Append(c);
                }
            }
        }

        return parsedAnyQuote && !inQuotes ? sb.ToString() : trimmed;
    }

    private static List<string> SplitExpectedLines(string? expectedValue) {
        if (string.IsNullOrWhiteSpace(expectedValue)) {
            return new List<string>();
        }

        return expectedValue
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string? GetMiscValue(MonitoredDomain domain, string key) {
        if (domain.MiscDnsValues == null || domain.MiscDnsValues.Count == 0) {
            return null;
        }

        if (domain.MiscDnsValues.TryGetValue(key, out string? directVal)) {
            return directVal;
        }

        foreach (KeyValuePair<string, string> kvp in domain.MiscDnsValues) {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)) {
                return kvp.Value;
            }
        }

        return null;
    }

    private static string FormatDnsOutcomeReport(MonitoredDomain domain, DnsQueryOutcome outcome) {
        StringBuilder sb = new();
        sb.AppendLine($"DNS Query: {outcome.QueryHost} ({outcome.QueryType})");
        sb.AppendLine($"Resolver: {outcome.ResolverUsed}");
        sb.AppendLine($"Status: {outcome.StatusCode}");
        sb.AppendLine($"Strict Mode: {domain.StrictDnsCheck} | Allow Absent: {domain.AllowDnsAbsent}");
        if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage)) {
            sb.AppendLine($"Error: {outcome.ErrorMessage}");
        }
        if (outcome.CnameChain.Count > 0) {
            sb.AppendLine($"CNAME Chain: {string.Join(" -> ", outcome.CnameChain)}");
        }
        sb.AppendLine();
        sb.AppendLine($"Live Answer Records ({outcome.Records.Count}):");
        if (outcome.Records.Count == 0) {
            sb.AppendLine("  <none>");
        } else {
            foreach (DnsRecordEntry rec in outcome.Records) {
                sb.AppendLine($"  {rec.Name} {rec.Ttl} IN {rec.Type} {rec.Value}");
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceSplitRegex();

    [GeneratedRegex(@"^(?<flags>\d+)\s+(?<tag>[A-Za-z0-9]+)\s+(?<val>.+)$")]
    private static partial Regex CaaRegex();

    /* :: :: DNS Value Normalization Helpers :: END :: */
    // //
    /* :: :: Alerting & Html Helpers :: START :: */

    private async Task SendMailChannelsAlertAsync(MonitoredDomain domain, string message, CancellationToken ct) {
        var payload = new {
            personalizations = new[] {
                new { to = new[] { new { email = "alert@yggdrasil.au", name = "Admin" } } }
            },
            from = new { email = "monitor@yggdrasil.au", name = "NetState" },
            subject = $"NetState: {domain.Name} is {domain.LastStatus}",
            content = new[] {
                new { type = "text/plain", value = message }
            }
        };

        try {
            HttpResponseMessage response = await _mailClient.PostAsJsonAsync("https://mail.yggdrasil.au/send", payload, ct);
            if (!response.IsSuccessStatusCode) {
                _logger.LogError("Failed to send MailChannels alert for {Domain}. Status: {Status}", domain.Name, response.StatusCode);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Exception while sending MailChannels alert for {Domain}", domain.Name);
        }
    }

    private static string ComputeSha256Hash(string rawData) {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<string> GetCleanHtmlAsync(string html) {
        AngleSharp.IConfiguration config = Configuration.Default;
        IBrowsingContext context = BrowsingContext.New(config);
        IHtmlParser parser = context.GetService<IHtmlParser>()!;
        IDocument document = await parser.ParseDocumentAsync(html);

        IHtmlCollection<IElement> elementsToRemove = document.QuerySelectorAll("input[type='hidden'], script, style");
        foreach (IElement element in elementsToRemove) {
            element.Remove();
        }

        return document.Body?.InnerHtml ?? string.Empty;
    }

    /* :: :: Alerting & Html Helpers :: END :: */
}

/// <summary>
/// Result payload returned by server-assisted zone discovery for UI host/DNS import workflows.
/// </summary>
/// <param name="MainHostname">The normalized main hostname discovered.</param>
/// <param name="WildcardDetected">True if wildcard DNS records were detected.</param>
/// <param name="CrtShStatus">Status text of best-effort Certificate Transparency log lookup.</param>
/// <param name="ApexRecords">Live DNS records found at the apex hostname.</param>
/// <param name="DiscoveredSubdomains">Resolving subdomains and summary record strings.</param>
public sealed record ZoneDiscoveryResult(
    string MainHostname,
    bool WildcardDetected,
    string CrtShStatus,
    Dictionary<string, List<DnsRecordEntry>> ApexRecords,
    Dictionary<string, List<string>> DiscoveredSubdomains
);

/* :: :: DomainChecker :: END :: */

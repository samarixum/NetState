using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using NetState.Shared.Models;

namespace NetState.Client.Services;

/* :: :: Client DTOs :: START :: */

/// <summary>
/// Represents a discovered DNS resource record returned by the server zone discovery endpoint.
/// </summary>
public sealed class DnsRecordDto {
    /// <summary>Gets or sets the record owner hostname.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the DNS record type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the DNS section name.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Gets or sets the DNS record TTL in seconds.</summary>
    public uint Ttl { get; set; }

    /// <summary>Gets or sets the normalized record value.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Represents candidate hosts and apex DNS records discovered by the server for a base domain.
/// </summary>
public sealed class ZoneDiscoveryDto {
    /// <summary>Gets or sets the normalized main hostname.</summary>
    public string MainHostname { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether wildcard DNS was detected.</summary>
    public bool WildcardDetected { get; set; }

    /// <summary>Gets or sets the status summary of best-effort crt.sh discovery.</summary>
    public string CrtShStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the live apex DNS records grouped by record type.</summary>
    public Dictionary<string, List<DnsRecordDto>> ApexRecords { get; set; } = new();

    /// <summary>Gets or sets the resolving subdomains discovered for the zone.</summary>
    public Dictionary<string, List<string>> DiscoveredSubdomains { get; set; } = new();
}

/* :: :: Client DTOs :: END :: */
// //
/* :: :: MonitoringApiClient :: START :: */

/// <summary>
/// HTTP API client used by the Avalonia desktop application to communicate with NetState.Server.
/// </summary>
public class MonitoringApiClient {
    private readonly HttpClient _httpClient;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitoringApiClient"/> class.
    /// </summary>
    /// <param name="baseUrl">The server base URL (for example http://localhost:5138/).</param>
    public MonitoringApiClient(string baseUrl) {
        _httpClient = new HttpClient {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: API Methods :: START :: */

    /// <summary>
    /// Retrieves all monitored domains and DNS entries from the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of monitored entries.</returns>
    public async Task<List<MonitoredDomain>> GetDomainsAsync(CancellationToken ct = default) {
        return await _httpClient.GetFromJsonAsync<List<MonitoredDomain>>("api/domains", ct) ?? new List<MonitoredDomain>();
    }

    /// <summary>
    /// Creates a new monitored domain or DNS entry on the server.
    /// </summary>
    /// <param name="domain">The entry to create.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateDomainAsync(MonitoredDomain domain, CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync("api/domains", domain, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deletes a monitored entry by identifier.
    /// </summary>
    /// <param name="id">The entry identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteDomainAsync(Guid id, CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.DeleteAsync($"api/domains/{id}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Triggers an immediate server check for a single monitored entry.
    /// </summary>
    /// <param name="id">The entry identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CheckDomainAsync(Guid id, CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.PostAsync($"api/domains/{id}/check", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Triggers immediate parallel checks for all monitored entries on the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CheckAllDomainsAsync(CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.PostAsync("api/domains/check-all", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Triggers immediate parallel checks for all entries grouped under a main hostname.
    /// </summary>
    /// <param name="mainHostname">The main hostname group to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CheckGroupAsync(string mainHostname, CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.PostAsync($"api/domains/check-group/{Uri.EscapeDataString(mainHostname)}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Updates an existing monitored entry on the server.
    /// </summary>
    /// <param name="domain">The updated entry model.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateDomainAsync(MonitoredDomain domain, CancellationToken ct = default) {
        using HttpResponseMessage response = await _httpClient.PutAsJsonAsync($"api/domains/{domain.Id}", domain, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Invokes server-assisted DNS and subdomain discovery for a base domain.
    /// </summary>
    /// <param name="domain">The base domain name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Discovered apex records and candidate subdomains.</returns>
    public async Task<ZoneDiscoveryDto?> DiscoverZoneAsync(string domain, CancellationToken ct = default) {
        return await _httpClient.GetFromJsonAsync<ZoneDiscoveryDto>($"api/dns/discover/{Uri.EscapeDataString(domain)}", ct);
    }

    /* :: :: API Methods :: END :: */
}

/* :: :: MonitoringApiClient :: END :: */

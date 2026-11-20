using System;
using System.Collections.Generic;
using System.Linq;
using NetState.Shared.Models;
using ReactiveUI;

namespace NetState.Client.ViewModels;

/* :: :: InspectDomain Models :: START :: */

/// <summary>
/// Strongly typed key-value display row for Avalonia compiled bindings in inspect grids.
/// </summary>
/// <param name="Key">The metadata or header key.</param>
/// <param name="Value">The associated value.</param>
public sealed record KeyValueRow(string Key, string Value);

/// <summary>
/// View model for inspecting captured headers, bodies, expected configurations, and misc DNS values.
/// </summary>
public class InspectDomainViewModel : ReactiveObject {
    private MonitoredDomain _domain;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="InspectDomainViewModel"/> class.
    /// </summary>
    /// <param name="domain">The monitored domain or DNS entry to inspect.</param>
    public InspectDomainViewModel(MonitoredDomain domain) {
        _domain = domain;
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    /// <summary>
    /// Gets or sets the inspected monitored entry.
    /// </summary>
    public MonitoredDomain Domain {
        get => _domain;
        set => this.RaiseAndSetIfChanged(ref _domain, value);
    }

    /// <summary>
    /// Gets a summary line describing the check configuration and target.
    /// </summary>
    public string ConfigurationSummary =>
        $"Main Hostname: {Domain.MainHostname ?? "<auto>"}  |  Kind: {Domain.EntryKind}  |  Strategy: {Domain.Expectation}" +
        (string.IsNullOrWhiteSpace(Domain.DnsRecordType) ? string.Empty : $" ({Domain.DnsRecordType})  |  Strict={Domain.StrictDnsCheck}, AllowAbsent={Domain.AllowDnsAbsent}");

    /// <summary>
    /// Gets the expected value text or fallback indicator.
    /// </summary>
    public string ExpectedValueText =>
        string.IsNullOrWhiteSpace(Domain.ExpectedValue)
            ? (Domain.AllowDnsAbsent ? "<Expected Absent / NXDOMAIN>" : "<None>")
            : Domain.ExpectedValue;

    /// <summary>
    /// Gets the last captured error message, or "None" when healthy.
    /// </summary>
    public string LastErrorText => Domain.LastError ?? "None";

    /// <summary>
    /// Gets the captured response body or formatted DNS lookup report.
    /// </summary>
    public string ResponseBody => Domain.LastResponseBody ?? "No response body captured.";

    /// <summary>
    /// Gets the last captured response headers or DNS response metadata rows.
    /// </summary>
    public List<KeyValueRow> Headers =>
        Domain.LastResponseHeaders?
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyValueRow(k.Key, k.Value))
            .ToList() ?? new List<KeyValueRow>();

    /// <summary>
    /// Gets the configured expected HTTP response headers.
    /// </summary>
    public List<KeyValueRow> ExpectedHeaderRows =>
        Domain.ExpectedHeaders?
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyValueRow(k.Key, k.Value))
            .ToList() ?? new List<KeyValueRow>();

    /// <summary>
    /// Gets the configured miscellaneous DNS values and validation directives.
    /// </summary>
    public List<KeyValueRow> MiscDnsRows =>
        Domain.MiscDnsValues?
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyValueRow(k.Key, k.Value))
            .ToList() ?? new List<KeyValueRow>();

    /// <summary>
    /// Gets the expander header label for miscellaneous DNS values.
    /// </summary>
    public string MiscDnsHeader => $"Misc DNS Values ({MiscDnsRows.Count})";

    /// <summary>
    /// Gets the expander header label for expected HTTP headers.
    /// </summary>
    public string ExpectedHeadersTitle => $"Expected Headers ({ExpectedHeaderRows.Count})";

    /// <summary>
    /// Gets the expander header label for response headers or DNS metadata.
    /// </summary>
    public string ResponseHeadersTitle => $"Response Headers / DNS Metadata ({Headers.Count})";

    /* :: :: Properties :: END :: */
}

/* :: :: InspectDomain Models :: END :: */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using NetState.Shared.Models;

namespace NetState.Client.Converters;

/* :: :: Domain Display Converters :: START :: */

/// <summary>
/// Converts a <see cref="MonitoredDomain"/> into a compact type/strategy badge string for dashboard display.
/// </summary>
public sealed class DomainTypeLabelConverter : IValueConverter {
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not MonitoredDomain domain) {
            return string.Empty;
        }

        return domain.Expectation switch {
            ExpectationType.DnsRecord => domain.AllowDnsAbsent
                ? $"DNS {(domain.DnsRecordType ?? "A").ToUpperInvariant()} (Absent)"
                : domain.StrictDnsCheck
                    ? $"DNS {(domain.DnsRecordType ?? "A").ToUpperInvariant()} (Strict)"
                    : $"DNS {(domain.DnsRecordType ?? "A").ToUpperInvariant()}",
            ExpectationType.ZoneAudit => "ZoneAudit",
            ExpectationType.HttpStatus => $"HTTP {domain.ExpectedValue ?? "*"}",
            ExpectationType.Redirect => "Redirect",
            ExpectationType.HtmlHash => "HtmlHash",
            _ => domain.Expectation.ToString()
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a <see cref="MonitoredDomain"/> into a single-line summary of its expected target, error, and misc DNS metadata.
/// </summary>
public sealed class DomainSummaryConverter : IValueConverter {
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not MonitoredDomain domain) {
            return string.Empty;
        }

        List<string> parts = new();

        if (!string.IsNullOrWhiteSpace(domain.ExpectedValue)) {
            string singleLineExpected = domain.ExpectedValue.Replace("\r\n", ", ").Replace("\n", ", ").Trim();
            if (singleLineExpected.Length > 80) {
                singleLineExpected = $"{singleLineExpected[..77]}...";
            }
            parts.Add($"Expect: {singleLineExpected}");
        } else if (domain.AllowDnsAbsent) {
            parts.Add("Expect: <Absent / NXDOMAIN>");
        } else if (domain.Expectation == ExpectationType.ZoneAudit) {
            parts.Add("Validate no extra DNS/subdomains");
        }

        if (domain.MiscDnsValues != null && domain.MiscDnsValues.Count > 0) {
            IEnumerable<string> meta = domain.MiscDnsValues
                .Take(3)
                .Select(kvp => $"{kvp.Key}={kvp.Value}");
            parts.Add($"[{string.Join(" · ", meta)}{(domain.MiscDnsValues.Count > 3 ? " …" : string.Empty)}]");
        }

        if (!string.IsNullOrWhiteSpace(domain.LastError)) {
            string err = domain.LastError.Replace("\r", " ").Replace("\n", " ").Trim();
            if (err.Length > 90) {
                err = $"{err[..87]}...";
            }
            parts.Add($"Err: {err}");
        }

        return string.Join("  |  ", parts);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a <see cref="MonitoredDomain"/> into an informative multi-line tooltip string.
/// </summary>
public sealed class DomainTooltipConverter : IValueConverter {
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not MonitoredDomain domain) {
            return string.Empty;
        }

        List<string> lines = new() {
            $"{domain.Name} ({domain.EntryKind})",
            $"Target: {domain.Url}",
            $"Main Hostname: {domain.MainHostname ?? "<auto>"}",
            $"Expectation: {domain.Expectation}"
        };

        if (!string.IsNullOrWhiteSpace(domain.DnsRecordType)) {
            lines.Add($"DNS Type: {domain.DnsRecordType} (Strict={domain.StrictDnsCheck}, AllowAbsent={domain.AllowDnsAbsent})");
        }

        if (!string.IsNullOrWhiteSpace(domain.ExpectedValue)) {
            lines.Add($"Expected Value:\n{domain.ExpectedValue}");
        }

        if (domain.MiscDnsValues != null && domain.MiscDnsValues.Count > 0) {
            lines.Add($"Misc DNS Values ({domain.MiscDnsValues.Count}):");
            foreach (KeyValuePair<string, string> kvp in domain.MiscDnsValues) {
                lines.Add($"  {kvp.Key}: {kvp.Value}");
            }
        }

        if (!string.IsNullOrWhiteSpace(domain.LastError)) {
            lines.Add($"Last Error: {domain.LastError}");
        }

        return string.Join("\n", lines);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

/* :: :: Domain Display Converters :: END :: */

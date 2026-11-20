using System;
using System.Collections.Generic;
using System.Linq;
using NetState.Shared.Models;

namespace NetState.Shared.Core;

/* :: :: DomainGroupingHelper :: START :: */

/// <summary>
/// Provides utilities for extracting hostnames and grouping monitored entries under their main hostname.
/// </summary>
public static class DomainGroupingHelper {
    private static readonly HashSet<string> SecondLevelDomains = new(StringComparer.OrdinalIgnoreCase) {
        "co", "com", "net", "org", "edu", "gov", "asn", "id"
    };

    /* :: :: Methods :: START :: */

    /// <summary>
    /// Extracts the hostname portion from a URL or bare hostname string.
    /// </summary>
    /// <param name="urlOrHost">An HTTP(S) URL or bare DNS hostname.</param>
    /// <returns>The normalized lowercase hostname without port or trailing dot.</returns>
    public static string ExtractHost(string? urlOrHost) {
        if (string.IsNullOrWhiteSpace(urlOrHost)) {
            return string.Empty;
        }

        string trimmed = urlOrHost.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)) {
            return uri.Host.TrimEnd('.').ToLowerInvariant();
        }

        if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out Uri? fallbackUri) && !string.IsNullOrWhiteSpace(fallbackUri.Host)) {
            return fallbackUri.Host.TrimEnd('.').ToLowerInvariant();
        }

        return trimmed.TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the main (registrable) hostname for a URL or FQDN.
    /// </summary>
    /// <param name="urlOrHost">An HTTP(S) URL or DNS FQDN.</param>
    /// <param name="knownMainHosts">Optional known main hostnames to match by suffix first.</param>
    /// <returns>The inferred main hostname in lowercase.</returns>
    public static string ExtractMainHostname(string? urlOrHost, IEnumerable<string>? knownMainHosts = null) {
        string host = ExtractHost(urlOrHost);
        if (string.IsNullOrWhiteSpace(host)) {
            return "ungrouped";
        }

        if (knownMainHosts != null) {
            string? bestMatch = knownMainHosts
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().TrimEnd('.').ToLowerInvariant())
                .OrderByDescending(k => k.Length)
                .FirstOrDefault(k => host.Equals(k, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{k}", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(bestMatch)) {
                return bestMatch;
            }
        }

        string[] parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) {
            return host;
        }

        string tld = parts[^1];
        string sld = parts[^2];
        if (tld.Length == 2 && SecondLevelDomains.Contains(sld) && parts.Length >= 3) {
            return string.Join('.', parts[^3..]);
        }

        return string.Join('.', parts[^2..]);
    }

    /// <summary>
    /// Resolves the effective main hostname for a <see cref="MonitoredDomain"/> entry.
    /// </summary>
    /// <param name="domain">The monitored domain entry.</param>
    /// <param name="knownMainHosts">Optional known main hostnames.</param>
    /// <returns>The effective lowercase main hostname.</returns>
    public static string ResolveMainHostname(MonitoredDomain domain, IEnumerable<string>? knownMainHosts = null) {
        if (!string.IsNullOrWhiteSpace(domain.MainHostname)) {
            return ExtractHost(domain.MainHostname);
        }

        return ExtractMainHostname(domain.Url, knownMainHosts);
    }

    /// <summary>
    /// Normalizes a DNS hostname or FQDN target value (lowercased without trailing dot).
    /// </summary>
    /// <param name="hostname">The hostname string.</param>
    /// <returns>The normalized hostname.</returns>
    public static string NormalizeDnsName(string? hostname) {
        if (string.IsNullOrWhiteSpace(hostname)) {
            return string.Empty;
        }

        return hostname.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /* :: :: Methods :: END :: */
}

/* :: :: DomainGroupingHelper :: END :: */

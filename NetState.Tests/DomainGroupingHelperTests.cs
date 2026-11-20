using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetState.Shared.Core;
using NetState.Shared.Models;

namespace NetState.Tests;

/* :: :: DomainGroupingHelperTests :: START :: */

/// <summary>
/// Unit tests verifying hostname extraction and main-hostname grouping rules.
/// </summary>
[TestClass]
public class DomainGroupingHelperTests {
    /* :: :: Test Methods :: START :: */

    /// <summary>
    /// Verifies that host extraction strips schemes, ports, paths, and trailing dots cleanly.
    /// </summary>
    [TestMethod]
    public void ExtractHostStripsSchemePortPathAndTrailingDot() {
        Assert.AreEqual("api.anime-dimension.com", DomainGroupingHelper.ExtractHost("https://api.anime-dimension.com:8443/v1/health."));
        Assert.AreEqual("_dmarc.yggdrasil.au", DomainGroupingHelper.ExtractHost("  _dmarc.yggdrasil.au. "));
        Assert.AreEqual(string.Empty, DomainGroupingHelper.ExtractHost("   "));
    }

    /// <summary>
    /// Verifies that URLs and hostnames containing underscores extract cleanly without leaving schemes or paths intact.
    /// </summary>
    [TestMethod]
    public void ExtractHostHandlesUnderscoresInUrlsAndServiceRecords() {
        Assert.AreEqual("dev_api.anime-dimension.com", DomainGroupingHelper.ExtractHost("https://dev_api.anime-dimension.com"));
        Assert.AreEqual("dev_api.anime-dimension.com", DomainGroupingHelper.ExtractHost("https://dev_api.anime-dimension.com:443/v1/test"));
        Assert.AreEqual("dev_api.anime-dimension.com", DomainGroupingHelper.ExtractHost("dev_api.anime-dimension.com"));
        Assert.AreEqual("_sipfederationtls._tcp.anime-dimension.com", DomainGroupingHelper.ExtractHost("_sipfederationtls._tcp.anime-dimension.com."));
    }

    /// <summary>
    /// Verifies that two-label <c>.au</c> domains remain intact as main hostnames for subdomains and service labels.
    /// </summary>
    [TestMethod]
    public void ExtractMainHostnamePreservesTwoLabelAuDomains() {
        Assert.AreEqual("yggdrasil.au", DomainGroupingHelper.ExtractMainHostname("https://admin.yggdrasil.au"));
        Assert.AreEqual("yggdrasil.au", DomainGroupingHelper.ExtractMainHostname("cf2024-1._domainkey.yggdrasil.au"));
        Assert.AreEqual("spendsmart.au", DomainGroupingHelper.ExtractMainHostname("_dmarc.spendsmart.au"));
    }

    /// <summary>
    /// Verifies that common multi-label public suffixes such as <c>.com.au</c> and <c>.co.uk</c> retain three labels for the main hostname.
    /// </summary>
    [TestMethod]
    public void ExtractMainHostnameHandlesSecondLevelCcTlds() {
        Assert.AreEqual("example.com.au", DomainGroupingHelper.ExtractMainHostname("https://api.example.com.au/status"));
        Assert.AreEqual("service.co.uk", DomainGroupingHelper.ExtractMainHostname("sub.service.co.uk"));
    }

    /// <summary>
    /// Verifies that known main hostnames match by longest domain suffix before falling back to heuristic extraction.
    /// </summary>
    [TestMethod]
    public void ExtractMainHostnameMatchesKnownHostsByLongestSuffix() {
        string[] known = { "anime-dimension.com", "dev.anime-dimension.com" };
        Assert.AreEqual("dev.anime-dimension.com", DomainGroupingHelper.ExtractMainHostname("sub.dev.anime-dimension.com", known));
        Assert.AreEqual("anime-dimension.com", DomainGroupingHelper.ExtractMainHostname("https://mail.anime-dimension.com", known));
    }

    /// <summary>
    /// Verifies that <see cref="DomainGroupingHelper.ResolveMainHostname"/> honors an explicit <see cref="MonitoredDomain.MainHostname"/> override.
    /// </summary>
    [TestMethod]
    public void ResolveMainHostnamePrefersExplicitMainHostnameProperty() {
        MonitoredDomain entry = new() {
            Name = "Custom Worker",
            Url = "https://mail-superposition-ml.sjk17.workers.dev",
            MainHostname = "superposition.ml."
        };

        Assert.AreEqual("superposition.ml", DomainGroupingHelper.ResolveMainHostname(entry));
    }

    /* :: :: Test Methods :: END :: */
}

/* :: :: DomainGroupingHelperTests :: END :: */

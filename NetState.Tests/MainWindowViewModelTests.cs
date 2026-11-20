using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetState.Client.ViewModels;
using NetState.Shared.Models;

namespace NetState.Tests;

/* :: :: MainWindowViewModelTests :: START :: */

/// <summary>
/// Unit tests verifying grouped main-hostname dashboard structure, DNS placement, host dropdowns, and state persistence.
/// </summary>
[TestClass]
public class MainWindowViewModelTests {
    /* :: :: Test Methods :: START :: */

    /// <summary>
    /// Verifies that domains group under main hostnames with DNS entries at top level, hosts inside a collapsible dropdown, and expansion state preserved across refreshes.
    /// </summary>
    [TestMethod]
    public void ApplyDomainsGroupsDnsDirectlyUnderMainHostnameAndHostsInDropdown() {
        MainWindowViewModel vm = new(apiClient: null, startAutoRefresh: false);

        List<MonitoredDomain> sample = new() {
            new MonitoredDomain {
                Name = "anime-dimension.com",
                Url = "https://anime-dimension.com",
                MainHostname = "anime-dimension.com",
                EntryKind = MonitoredEntryKind.Host,
                Expectation = ExpectationType.HttpStatus,
                ExpectedValue = "200",
                LastStatus = CheckStatus.Down
            },
            new MonitoredDomain {
                Name = "api.anime-dimension.com",
                Url = "https://api.anime-dimension.com",
                MainHostname = "anime-dimension.com",
                EntryKind = MonitoredEntryKind.Host,
                Expectation = ExpectationType.HttpStatus,
                ExpectedValue = "200",
                LastStatus = CheckStatus.Down
            },
            new MonitoredDomain {
                Name = "TXT _dmarc.anime-dimension.com",
                Url = "_dmarc.anime-dimension.com",
                MainHostname = "anime-dimension.com",
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.DnsRecord,
                DnsRecordType = "TXT",
                ExpectedValue = "v=DMARC1; p=quarantine;",
                LastStatus = CheckStatus.Healthy
            },
            new MonitoredDomain {
                Name = "[Zone Audit] anime-dimension.com",
                Url = "anime-dimension.com",
                MainHostname = "anime-dimension.com",
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.ZoneAudit,
                LastStatus = CheckStatus.Healthy
            },
            new MonitoredDomain {
                Name = "ygg",
                Url = "https://yggdrasil.au",
                MainHostname = "yggdrasil.au",
                EntryKind = MonitoredEntryKind.Host,
                Expectation = ExpectationType.Redirect,
                ExpectedValue = "https://github.com/yggdrasil-au",
                LastStatus = CheckStatus.Healthy
            }
        };

        vm.ApplyDomains(sample);

        Assert.AreEqual(2, vm.MainHostnameGroups.Count);

        MainHostnameGroupViewModel animeGroup = vm.MainHostnameGroups[0];
        Assert.AreEqual("anime-dimension.com", animeGroup.MainHostname);
        Assert.AreEqual(2, animeGroup.DnsEntries.Count);
        Assert.AreEqual(ExpectationType.ZoneAudit, animeGroup.DnsEntries[0].Expectation);
        Assert.AreEqual(2, animeGroup.HostEntries.Count);
        Assert.IsFalse(animeGroup.IsHostsExpanded);
        Assert.AreEqual(CheckStatus.Down, animeGroup.AggregateStatus);

        // Expand hosts dropdown and verify state persists across simulated 30s auto-refresh
        animeGroup.IsHostsExpanded = true;
        vm.ApplyDomains(sample);

        Assert.IsTrue(vm.MainHostnameGroups[0].IsHostsExpanded);
        Assert.IsFalse(vm.MainHostnameGroups[1].IsHostsExpanded);

        // Verify search query filters by MiscDnsValues / URL / Name cleanly
        vm.SearchQuery = "yggdrasil";
        Assert.AreEqual(1, vm.MainHostnameGroups.Count);
        Assert.AreEqual("yggdrasil.au", vm.MainHostnameGroups.Single().MainHostname);
    }

    /* :: :: Test Methods :: END :: */
}

/* :: :: MainWindowViewModelTests :: END :: */

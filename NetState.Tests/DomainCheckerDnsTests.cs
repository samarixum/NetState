using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetState.Server.Services;
using NetState.Shared.Models;

namespace NetState.Tests;

/* :: :: DomainCheckerDnsTests :: START :: */

/// <summary>
/// Unit tests validating strict DNS matching, misc DNS values, zone auditing, and HTTP checks.
/// </summary>
[TestClass]
public class DomainCheckerDnsTests {
    /* :: :: Fake Test Doubles :: START :: */

    private sealed class FakeDnsResolver : IDnsResolver {
        private readonly Dictionary<(string Host, string Type), DnsQueryOutcome> _responses = new();

        public void SetRecords(string host, string type, params (string Value, uint Ttl)[] records) {
            List<DnsRecordEntry> list = new();
            foreach ((string val, uint ttl) in records) {
                list.Add(new DnsRecordEntry(host, type.ToUpperInvariant(), "Answer", ttl, val, Array.Empty<byte>()));
            }
            _responses[(host.ToLowerInvariant(), type.ToUpperInvariant())] = new DnsQueryOutcome(
                host.ToLowerInvariant(),
                type.ToUpperInvariant(),
                "FakeDns",
                true,
                list.Count > 0 ? "OK" : "NO_RECORDS",
                list,
                new List<string>()
            );
        }

        public void SetCnameChain(string host, string type, string cnameTarget, params (string Value, uint Ttl)[] records) {
            List<DnsRecordEntry> list = new();
            foreach ((string val, uint ttl) in records) {
                list.Add(new DnsRecordEntry(host, type.ToUpperInvariant(), "Answer", ttl, val, Array.Empty<byte>()));
            }
            _responses[(host.ToLowerInvariant(), type.ToUpperInvariant())] = new DnsQueryOutcome(
                host.ToLowerInvariant(),
                type.ToUpperInvariant(),
                "FakeDns",
                true,
                list.Count > 0 ? "OK" : "NO_RECORDS",
                list,
                new List<string> { cnameTarget }
            );
        }

        public void SetNxDomain(string host, string type) {
            _responses[(host.ToLowerInvariant(), type.ToUpperInvariant())] = new DnsQueryOutcome(
                host.ToLowerInvariant(),
                type.ToUpperInvariant(),
                "FakeDns",
                true,
                "NXDOMAIN",
                new List<DnsRecordEntry>(),
                new List<string>()
            );
        }

        public Task<DnsQueryOutcome> QueryAsync(
            string host,
            string recordType,
            string? dnsServer = null,
            bool bypassCache = true,
            CancellationToken ct = default
        ) {
            if (_responses.TryGetValue((host.ToLowerInvariant(), recordType.ToUpperInvariant()), out DnsQueryOutcome? outcome)) {
                return Task.FromResult(outcome);
            }

            return Task.FromResult(new DnsQueryOutcome(
                host.ToLowerInvariant(),
                recordType.ToUpperInvariant(),
                "FakeDns",
                true,
                "NO_RECORDS",
                new List<DnsRecordEntry>(),
                new List<string>()
            ));
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_responder(request));
        }
    }

    /* :: :: Fake Test Doubles :: END :: */
    // //
    /* :: :: DNS Record Tests :: START :: */

    /// <summary>
    /// Verifies canonical IPv6 normalization and order-insensitive set matching for strict DNS checks.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordExactSetMatchesCanonicalIpv6AndIgnoresOrder() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords(
            "anime-dimension.com",
            "AAAA",
            ("2606:4700:3036:0000:0000:0000:6815:0d1b", 300),
            ("2606:4700:3032::ac43:8478", 300)
        );

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());
        MonitoredDomain entry = new() {
            Name = "AAAA anime-dimension.com",
            Url = "anime-dimension.com",
            MainHostname = "anime-dimension.com",
            EntryKind = MonitoredEntryKind.DnsEntry,
            Expectation = ExpectationType.DnsRecord,
            DnsRecordType = "AAAA",
            StrictDnsCheck = true,
            ExpectedValue = "2606:4700:3032::ac43:8478\n2606:4700:3036::6815:d1b"
        };

        await checker.CheckDomainAsync(entry, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Healthy, entry.LastStatus);
        Assert.IsNull(entry.LastError);
    }

    /// <summary>
    /// Verifies that strict DNS validation fails and reports unexpected extra records returned by DNS.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordStrictModeFailsWhenUnregisteredRecordAppears() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords(
            "anime-dimension.com",
            "A",
            ("104.21.13.27", 300),
            ("172.67.132.120", 300),
            ("198.51.100.99", 300)
        );

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());
        MonitoredDomain entry = new() {
            Name = "A anime-dimension.com",
            Url = "anime-dimension.com",
            MainHostname = "anime-dimension.com",
            EntryKind = MonitoredEntryKind.DnsEntry,
            Expectation = ExpectationType.DnsRecord,
            DnsRecordType = "A",
            StrictDnsCheck = true,
            ExpectedValue = "104.21.13.27\n172.67.132.120"
        };

        await checker.CheckDomainAsync(entry, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Down, entry.LastStatus);
        StringAssert.Contains(entry.LastError ?? string.Empty, "Unregistered A found");
        StringAssert.Contains(entry.LastError ?? string.Empty, "198.51.100.99");
    }

    /// <summary>
    /// Verifies multi-segment quoted TXT concatenation and CAA value normalization during strict matching.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordNormalizesMultiSegmentQuotedTxtAndCaa() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords("cf2024-1._domainkey.anime-dimension.com", "TXT", ("v=DKIM1; k=rsa; p=PART1PART2", 300));
        resolver.SetRecords("anime-dimension.com", "CAA", ("0 issue \"letsencrypt.org\"", 3600));

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());

        MonitoredDomain dkim = new() {
            Name = "DKIM",
            Url = "cf2024-1._domainkey.anime-dimension.com",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "TXT",
            StrictDnsCheck = true,
            ExpectedValue = "\"v=DKIM1; k=rsa; p=PART1\" \"PART2\""
        };

        MonitoredDomain caa = new() {
            Name = "CAA",
            Url = "anime-dimension.com",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "CAA",
            StrictDnsCheck = true,
            ExpectedValue = "0 ISSUE letsencrypt.org"
        };

        await checker.CheckDomainAsync(dkim, CancellationToken.None, suppressAlerts: true);
        await checker.CheckDomainAsync(caa, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Healthy, dkim.LastStatus);
        Assert.AreEqual(CheckStatus.Healthy, caa.LastStatus);
    }

    /// <summary>
    /// Verifies wire RDATA decoding for CAA and miscellaneous/unknown DNS record types.
    /// </summary>
    [TestMethod]
    public void DecodeCaaAndUnknownWireRdataFormatsMiscRecordsCleanly() {
        // 0 issue "letsencrypt.org"
        byte[] caaBytes = Convert.FromHexString("000569737375656C657473656E63727970742E6F7267");
        string decodedCaa = SystemDnsResolver.DecodeCaaRdata(caaBytes);
        Assert.AreEqual("0 issue \"letsencrypt.org\"", decodedCaa);

        // Unknown record TYPE65000 fallback
        string decodedMisc = SystemDnsResolver.ParseWireRdata(65000, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, null, 0);
        Assert.AreEqual("\\# 4 deadbeef", decodedMisc);
    }

    /// <summary>
    /// Verifies record type parsing and formatting across standard names, miscellaneous types, and numeric codes.
    /// </summary>
    [TestMethod]
    public void ParseAndFormatRecordTypeCodeHandlesStandardAndMiscTypes() {
        Assert.AreEqual((ushort)257, SystemDnsResolver.ParseRecordTypeCode("caa"));
        Assert.AreEqual((ushort)65, SystemDnsResolver.ParseRecordTypeCode("HTTPS"));
        Assert.AreEqual((ushort)65, SystemDnsResolver.ParseRecordTypeCode("TYPE65"));
        Assert.AreEqual((ushort)257, SystemDnsResolver.ParseRecordTypeCode("257"));
        Assert.AreEqual((ushort)65000, SystemDnsResolver.ParseRecordTypeCode("TYPE65000"));
        Assert.AreEqual("CAA", SystemDnsResolver.FormatRecordTypeName(257));
        Assert.AreEqual("HTTPS", SystemDnsResolver.FormatRecordTypeName(65));
        Assert.AreEqual("TYPE65000", SystemDnsResolver.FormatRecordTypeName(65000));
    }

    /// <summary>
    /// Verifies that SOA validation enforces exact serial matching unless <c>IgnoreSoaSerial=true</c> is set in MiscDnsValues.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordSoaRespectsIgnoreSoaSerialMiscSetting() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords(
            "yggdrasil.au",
            "SOA",
            ("magdalena.ns.cloudflare.com dns.cloudflare.com 2999999999 10000 2400 604800 1800", 1800)
        );

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());

        MonitoredDomain strictSoa = new() {
            Name = "SOA Strict",
            Url = "yggdrasil.au",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "SOA",
            ExpectedValue = "magdalena.ns.cloudflare.com dns.cloudflare.com 2419861764 10000 2400 604800 1800"
        };

        MonitoredDomain ignoreSerialSoa = new() {
            Name = "SOA Ignore Serial",
            Url = "yggdrasil.au",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "SOA",
            ExpectedValue = "magdalena.ns.cloudflare.com dns.cloudflare.com 2419861764 10000 2400 604800 1800",
            MiscDnsValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["IgnoreSoaSerial"] = "true"
            }
        };

        await checker.CheckDomainAsync(strictSoa, CancellationToken.None, suppressAlerts: true);
        await checker.CheckDomainAsync(ignoreSerialSoa, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Down, strictSoa.LastStatus);
        Assert.AreEqual(CheckStatus.Healthy, ignoreSerialSoa.LastStatus);
    }

    /// <summary>
    /// Verifies that <see cref="MonitoredDomain.AllowDnsAbsent"/> passes on NXDOMAIN and alerts if a record appears.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordAllowDnsAbsentAlertsWhenUnexpectedRecordAppears() {
        FakeDnsResolver resolver = new();
        resolver.SetNxDomain("www.spendsmart.au", "A");
        resolver.SetRecords("rogue.spendsmart.au", "A", ("192.0.2.1", 300));

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());

        MonitoredDomain absentHealthy = new() {
            Name = "A www.spendsmart.au (absent)",
            Url = "www.spendsmart.au",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "A",
            AllowDnsAbsent = true
        };

        MonitoredDomain absentBroken = new() {
            Name = "A rogue.spendsmart.au (absent)",
            Url = "rogue.spendsmart.au",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "A",
            AllowDnsAbsent = true
        };

        await checker.CheckDomainAsync(absentHealthy, CancellationToken.None, suppressAlerts: true);
        await checker.CheckDomainAsync(absentBroken, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Healthy, absentHealthy.LastStatus);
        Assert.AreEqual(CheckStatus.Down, absentBroken.LastStatus);
    }

    /// <summary>
    /// Verifies MiscDnsValues assertions including <c>AssertContains</c> and <c>AssertMaxTtl</c>.
    /// </summary>
    [TestMethod]
    public async Task DnsRecordMiscAssertionsValidateMaxTtlAndContains() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords("_dmarc.spendsmart.au", "TXT", ("v=DMARC1; p=quarantine; rua=mailto:dmarc_rua@onsecureserver.net;", 3600));

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());

        MonitoredDomain failingTtl = new() {
            Name = "DMARC",
            Url = "_dmarc.spendsmart.au",
            Expectation = ExpectationType.DnsRecord,
            EntryKind = MonitoredEntryKind.DnsEntry,
            DnsRecordType = "TXT",
            ExpectedValue = "v=DMARC1; p=quarantine; rua=mailto:dmarc_rua@onsecureserver.net;",
            MiscDnsValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["AssertContains"] = "p=quarantine",
                ["AssertMaxTtl"] = "300"
            }
        };

        await checker.CheckDomainAsync(failingTtl, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Down, failingTtl.LastStatus);
        StringAssert.Contains(failingTtl.LastError ?? string.Empty, "AssertMaxTtl=300");
    }

    /* :: :: DNS Record Tests :: END :: */
    // //
    /* :: :: Zone Audit & HTTP Tests :: START :: */

    /// <summary>
    /// Verifies that zone audits pass on clean baselines and flag unregistered apex record types or resolving subdomains.
    /// </summary>
    [TestMethod]
    public async Task ZoneAuditPassesWhenCleanAndDetectsUnregisteredApexTypeOrSubdomain() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords("spendsmart.au", "NS", ("magdalena.ns.cloudflare.com", 86400), ("vasilii.ns.cloudflare.com", 86400));
        resolver.SetRecords("_dmarc.spendsmart.au", "TXT", ("v=DMARC1; p=quarantine;", 300));

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, new HttpClient(), new HttpClient());

        MonitoredDomain audit = new() {
            Name = "[Zone Audit] spendsmart.au",
            Url = "spendsmart.au",
            MainHostname = "spendsmart.au",
            EntryKind = MonitoredEntryKind.DnsEntry,
            Expectation = ExpectationType.ZoneAudit
        };

        List<MonitoredDomain> registered = new() {
            audit,
            new MonitoredDomain {
                Name = "NS spendsmart.au",
                Url = "spendsmart.au",
                MainHostname = "spendsmart.au",
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.DnsRecord,
                DnsRecordType = "NS",
                ExpectedValue = "magdalena.ns.cloudflare.com\nvasilii.ns.cloudflare.com"
            },
            new MonitoredDomain {
                Name = "TXT _dmarc.spendsmart.au",
                Url = "_dmarc.spendsmart.au",
                MainHostname = "spendsmart.au",
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.DnsRecord,
                DnsRecordType = "TXT",
                ExpectedValue = "v=DMARC1; p=quarantine;"
            }
        };

        // 1. Clean baseline
        await checker.CheckDomainAsync(audit, CancellationToken.None, registered, suppressAlerts: true);
        Assert.AreEqual(CheckStatus.Healthy, audit.LastStatus, audit.LastError);

        // 2. Introduce unregistered apex A record and unregistered staging subdomain
        resolver.SetRecords("spendsmart.au", "A", ("203.0.113.10", 300));
        resolver.SetRecords("staging.spendsmart.au", "A", ("203.0.113.20", 300));

        await checker.CheckDomainAsync(audit, CancellationToken.None, registered, suppressAlerts: true);
        Assert.AreEqual(CheckStatus.Down, audit.LastStatus);
        StringAssert.Contains(audit.LastError ?? string.Empty, "Unregistered A record(s) on spendsmart.au");
        StringAssert.Contains(audit.LastError ?? string.Empty, "staging.spendsmart.au");
    }

    /// <summary>
    /// Verifies that transient HTTP 502 errors from crt.sh do not cause false-positive Down statuses on zone audits.
    /// </summary>
    [TestMethod]
    public async Task ZoneAuditIgnoresCrtShUpstream502ErrorsGracefully() {
        FakeDnsResolver resolver = new();
        resolver.SetRecords("spendsmart.au", "NS", ("magdalena.ns.cloudflare.com", 86400));

        using HttpClient brokenCrtHttp = new(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, resolver, brokenCrtHttp, new HttpClient());

        MonitoredDomain audit = new() {
            Name = "[Zone Audit] spendsmart.au",
            Url = "spendsmart.au",
            MainHostname = "spendsmart.au",
            EntryKind = MonitoredEntryKind.DnsEntry,
            Expectation = ExpectationType.ZoneAudit,
            MiscDnsValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["EnableCrtSh"] = "true"
            }
        };

        List<MonitoredDomain> registered = new() {
            audit,
            new MonitoredDomain {
                Name = "NS",
                Url = "spendsmart.au",
                MainHostname = "spendsmart.au",
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.DnsRecord,
                DnsRecordType = "NS",
                ExpectedValue = "magdalena.ns.cloudflare.com"
            }
        };

        await checker.CheckDomainAsync(audit, CancellationToken.None, registered, suppressAlerts: true);
        Assert.AreEqual(CheckStatus.Healthy, audit.LastStatus, audit.LastError);
        StringAssert.Contains(audit.LastResponseBody ?? string.Empty, "HTTP 502");
    }

    /// <summary>
    /// Verifies HTTP 308 PermanentRedirect target matching and case-insensitive response header validation.
    /// </summary>
    [TestMethod]
    public async Task HttpCheckMatchesRedirect308AndCaseInsensitiveHeaders() {
        using HttpClient stubHttp = new(new StubHttpMessageHandler(_ => {
            HttpResponseMessage msg = new(HttpStatusCode.PermanentRedirect);
            msg.Headers.Location = new Uri("https://github.com/yggdrasil-au");
            msg.Headers.TryAddWithoutValidation("server", "cloudflare");
            msg.Content = new StringContent("Redirecting", Encoding.UTF8, "text/plain");
            return msg;
        }));

        DomainChecker checker = new(NullLogger<DomainChecker>.Instance, new FakeDnsResolver(), stubHttp, new HttpClient());
        MonitoredDomain entry = new() {
            Name = "ygg",
            Url = "https://yggdrasil.au",
            MainHostname = "yggdrasil.au",
            Expectation = ExpectationType.Redirect,
            ExpectedValue = "https://github.com/yggdrasil-au",
            ExpectedHeaders = new Dictionary<string, string> {
                ["Server"] = "cloudflare"
            }
        };

        await checker.CheckDomainAsync(entry, CancellationToken.None, suppressAlerts: true);

        Assert.AreEqual(CheckStatus.Healthy, entry.LastStatus, entry.LastError);
    }

    /* :: :: Zone Audit & HTTP Tests :: END :: */
}

/* :: :: DomainCheckerDnsTests :: END :: */

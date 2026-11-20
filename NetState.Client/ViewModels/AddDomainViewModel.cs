using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using NetState.Client.Services;
using NetState.Shared.Core;
using NetState.Shared.Models;
using ReactiveUI;

namespace NetState.Client.ViewModels;

/* :: :: SelectableHost :: START :: */

/// <summary>
/// Represents a discovered host or DNS record candidate that can be batch-added to NetState.
/// </summary>
public class SelectableHost : ReactiveObject {
    private bool _isSelected;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectableHost"/> class.
    /// </summary>
    /// <param name="domain">The candidate monitored entry.</param>
    public SelectableHost(MonitoredDomain domain) {
        Domain = domain;
        IsSelected = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether this candidate will be added.
    /// </summary>
    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>
    /// Gets the candidate domain or DNS entry.
    /// </summary>
    public MonitoredDomain Domain { get; }
}

/* :: :: SelectableHost :: END :: */
// //
/* :: :: AddDomainViewModel :: START :: */

/// <summary>
/// View model for creating, editing, and discovering monitored HTTP hosts, DNS records, and zone audits.
/// </summary>
public class AddDomainViewModel : ReactiveObject {
    private readonly MonitoredDomain? _existingDomain;
    private readonly MonitoringApiClient? _apiClient;
    private Dictionary<string, string> _expectedHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _miscDnsValues = new(StringComparer.OrdinalIgnoreCase);

    private string _domainName = string.Empty;
    private string _url = string.Empty;
    private string _mainHostname = string.Empty;
    private bool _mainHostnameEditedManually;
    private MonitoredEntryKind _selectedEntryKind = MonitoredEntryKind.Host;
    private ExpectationType _selectedCheckType = ExpectationType.HttpStatus;
    private string _dnsRecordType = "A";
    private string? _selectedDnsTypePreset = "A";
    private bool _strictDnsCheck = true;
    private bool _allowDnsAbsent;
    private string _expectedValue = string.Empty;
    private string _searchDomain = string.Empty;
    private string _discoverySummary = string.Empty;
    private ObservableCollection<SelectableHost> _resolvedHosts = new();

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="AddDomainViewModel"/> class.
    /// </summary>
    /// <param name="existing">Existing entry when editing, or null when creating a new entry.</param>
    /// <param name="defaultMainHostname">Optional pre-filled main hostname when adding from a group card.</param>
    /// <param name="defaultEntryKind">Optional pre-selected entry kind when adding from a group card.</param>
    /// <param name="apiClient">Optional server API client used for live server-side zone discovery.</param>
    public AddDomainViewModel(
        MonitoredDomain? existing = null,
        string? defaultMainHostname = null,
        MonitoredEntryKind? defaultEntryKind = null,
        MonitoringApiClient? apiClient = null
    ) {
        _existingDomain = existing;
        _apiClient = apiClient;

        if (existing != null) {
            _domainName = existing.Name;
            _url = existing.Url;
            _mainHostname = existing.MainHostname ?? DomainGroupingHelper.ExtractMainHostname(existing.Url);
            _mainHostnameEditedManually = true;
            _selectedEntryKind = existing.EntryKind;
            _selectedCheckType = existing.Expectation;
            _dnsRecordType = string.IsNullOrWhiteSpace(existing.DnsRecordType) ? "A" : existing.DnsRecordType.Trim().ToUpperInvariant();
            _selectedDnsTypePreset = AvailableDnsTypePresets.FirstOrDefault(p => string.Equals(p, _dnsRecordType, StringComparison.OrdinalIgnoreCase));
            _strictDnsCheck = existing.StrictDnsCheck;
            _allowDnsAbsent = existing.AllowDnsAbsent;
            _expectedValue = existing.ExpectedValue ?? string.Empty;
            _expectedHeaders = existing.ExpectedHeaders != null
                ? new Dictionary<string, string>(existing.ExpectedHeaders, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _miscDnsValues = existing.MiscDnsValues != null
                ? new Dictionary<string, string>(existing.MiscDnsValues, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        } else {
            if (!string.IsNullOrWhiteSpace(defaultMainHostname)) {
                _mainHostname = DomainGroupingHelper.ExtractHost(defaultMainHostname);
                _mainHostnameEditedManually = true;
                _searchDomain = _mainHostname;
            }

            if (defaultEntryKind.HasValue) {
                _selectedEntryKind = defaultEntryKind.Value;
                _selectedCheckType = defaultEntryKind.Value == MonitoredEntryKind.DnsEntry
                    ? ExpectationType.DnsRecord
                    : ExpectationType.HttpStatus;
            }
        }

        IObservable<bool> canSave = this.WhenAnyValue(
            x => x.DomainName,
            x => x.Url,
            x => x.ExpectedValue,
            x => x.SelectedCheckType,
            x => x.AllowDnsAbsent,
            (name, url, expected, checkType, allowAbsent) => {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) {
                    return false;
                }

                if (checkType is ExpectationType.HttpStatus or ExpectationType.HtmlHash) {
                    return !string.IsNullOrWhiteSpace(expected);
                }

                return true;
            }
        );

        SaveCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => {
            string cleanUrl = Url.Trim();
            string cleanMain = string.IsNullOrWhiteSpace(MainHostname)
                ? DomainGroupingHelper.ExtractMainHostname(cleanUrl)
                : DomainGroupingHelper.ExtractHost(MainHostname);

            string? normalizedDnsType = SelectedCheckType == ExpectationType.DnsRecord
                ? (string.IsNullOrWhiteSpace(DnsRecordType) ? "A" : DnsRecordType.Trim().ToUpperInvariant())
                : null;

            string? cleanExpected = string.IsNullOrWhiteSpace(ExpectedValue) ? null : ExpectedValue.Trim();

            if (_existingDomain != null) {
                _existingDomain.Name = DomainName.Trim();
                _existingDomain.Url = cleanUrl;
                _existingDomain.MainHostname = cleanMain;
                _existingDomain.EntryKind = SelectedEntryKind;
                _existingDomain.Expectation = SelectedCheckType;
                _existingDomain.DnsRecordType = normalizedDnsType;
                _existingDomain.StrictDnsCheck = StrictDnsCheck;
                _existingDomain.AllowDnsAbsent = AllowDnsAbsent;
                _existingDomain.ExpectedValue = cleanExpected;
                _existingDomain.ExpectedHeaders = _expectedHeaders;
                _existingDomain.MiscDnsValues = _miscDnsValues;
                return new List<MonitoredDomain> { _existingDomain };
            }

            return new List<MonitoredDomain> {
                new MonitoredDomain {
                    Name = DomainName.Trim(),
                    Url = cleanUrl,
                    MainHostname = cleanMain,
                    EntryKind = SelectedEntryKind,
                    Expectation = SelectedCheckType,
                    DnsRecordType = normalizedDnsType,
                    StrictDnsCheck = StrictDnsCheck,
                    AllowDnsAbsent = AllowDnsAbsent,
                    ExpectedValue = cleanExpected,
                    ExpectedHeaders = _expectedHeaders,
                    MiscDnsValues = _miscDnsValues
                }
            };
        }, canSave);

        AddSelectedCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => {
            List<MonitoredDomain> selected = ResolvedHosts.Where(h => h.IsSelected).Select(h => h.Domain).ToList();
            return selected.Count > 0 ? selected : null;
        });

        OpenHeadersCommand = ReactiveCommand.CreateFromTask(async () => {
            Dictionary<string, string>? result = await OpenHeadersInteraction.Handle(_expectedHeaders);
            if (result != null) {
                _expectedHeaders = result;
                this.RaisePropertyChanged(nameof(HeadersButtonLabel));
            }
        });

        OpenMiscDnsValuesCommand = ReactiveCommand.CreateFromTask(async () => {
            Dictionary<string, string>? result = await OpenMiscDnsValuesInteraction.Handle(_miscDnsValues);
            if (result != null) {
                _miscDnsValues = result;
                this.RaisePropertyChanged(nameof(MiscDnsButtonLabel));
            }
        });

        CancelCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => null);

        ResolveCommand = ReactiveCommand.CreateFromTask(async () => {
            if (string.IsNullOrWhiteSpace(SearchDomain)) {
                return;
            }

            ResolvedHosts.Clear();
            string search = DomainGroupingHelper.ExtractHost(SearchDomain);
            DiscoverySummary = $"Discovering {search}...";

            if (_apiClient != null) {
                try {
                    ZoneDiscoveryDto? discovery = await _apiClient.DiscoverZoneAsync(search);
                    if (discovery != null) {
                        PopulateFromDiscoveryDto(discovery);
                        return;
                    }
                } catch (Exception ex) {
                    Diagnostics.Log($"Server discovery fallback triggered: {ex.Message}");
                }
            }

            await PopulateFallbackDiscoveryAsync(search);
        });
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    /// <summary>Gets the available validation strategies selectable in the dialog.</summary>
    public ExpectationType[] AvailableCheckTypes => new[] {
        ExpectationType.HttpStatus,
        ExpectationType.Redirect,
        ExpectationType.HtmlHash,
        ExpectationType.DnsRecord,
        ExpectationType.ZoneAudit
    };

    /// <summary>Gets the available UI placement sections under the main hostname.</summary>
    public MonitoredEntryKind[] AvailableEntryKinds => new[] {
        MonitoredEntryKind.Host,
        MonitoredEntryKind.DnsEntry
    };

    /// <summary>Gets common DNS record type presets including misc types such as CAA, HTTPS, SVCB, TLSA, and SSHFP.</summary>
    public string[] AvailableDnsTypePresets => new[] {
        "A", "AAAA", "CNAME", "MX", "TXT", "SRV", "NS", "CAA", "SOA",
        "HTTPS", "SVCB", "PTR", "SPF", "TLSA", "SSHFP", "DS", "ANY"
    };

    /// <summary>Gets or sets the display name of the monitored entry.</summary>
    public string DomainName {
        get => _domainName;
        set => this.RaiseAndSetIfChanged(ref _domainName, value);
    }

    /// <summary>Gets or sets the target HTTP URL or DNS hostname/FQDN.</summary>
    public string Url {
        get => _url;
        set {
            this.RaiseAndSetIfChanged(ref _url, value);
            if (!_mainHostnameEditedManually) {
                MainHostname = DomainGroupingHelper.ExtractMainHostname(value);
                _mainHostnameEditedManually = false;
            }
        }
    }

    /// <summary>Gets or sets the main hostname under which this entry is grouped.</summary>
    public string MainHostname {
        get => _mainHostname;
        set {
            _mainHostnameEditedManually = true;
            this.RaiseAndSetIfChanged(ref _mainHostname, value);
        }
    }

    /// <summary>Gets or sets whether this entry appears in the Hosts dropdown or directly under the main hostname as a DNS entry.</summary>
    public MonitoredEntryKind SelectedEntryKind {
        get => _selectedEntryKind;
        set => this.RaiseAndSetIfChanged(ref _selectedEntryKind, value);
    }

    /// <summary>Gets or sets the selected health-check validation strategy.</summary>
    public ExpectationType SelectedCheckType {
        get => _selectedCheckType;
        set {
            this.RaiseAndSetIfChanged(ref _selectedCheckType, value);
            if (_existingDomain == null) {
                SelectedEntryKind = value is ExpectationType.DnsRecord or ExpectationType.ZoneAudit
                    ? MonitoredEntryKind.DnsEntry
                    : MonitoredEntryKind.Host;
            }

            this.RaisePropertyChanged(nameof(IsDnsRecordMode));
            this.RaisePropertyChanged(nameof(IsDnsOrAuditMode));
            this.RaisePropertyChanged(nameof(IsHttpMode));
            this.RaisePropertyChanged(nameof(ExpectedValueWatermark));
        }
    }

    /// <summary>Gets or sets the DNS record type name or custom TYPEn string.</summary>
    public string DnsRecordType {
        get => _dnsRecordType;
        set => this.RaiseAndSetIfChanged(ref _dnsRecordType, value);
    }

    /// <summary>Gets or sets the preset selected in the DNS record type dropdown.</summary>
    public string? SelectedDnsTypePreset {
        get => _selectedDnsTypePreset;
        set {
            this.RaiseAndSetIfChanged(ref _selectedDnsTypePreset, value);
            if (!string.IsNullOrWhiteSpace(value)) {
                DnsRecordType = value;
            }
        }
    }

    /// <summary>Gets or sets whether extra unregistered DNS values fail strict DNS validation.</summary>
    public bool StrictDnsCheck {
        get => _strictDnsCheck;
        set => this.RaiseAndSetIfChanged(ref _strictDnsCheck, value);
    }

    /// <summary>Gets or sets whether the queried DNS record type is expected to remain absent (NXDOMAIN/NO_RECORDS).</summary>
    public bool AllowDnsAbsent {
        get => _allowDnsAbsent;
        set {
            this.RaiseAndSetIfChanged(ref _allowDnsAbsent, value);
            this.RaisePropertyChanged(nameof(ExpectedValueWatermark));
        }
    }

    /// <summary>Gets or sets the expected redirect URL, HTML hash, status code, or newline-delimited DNS values.</summary>
    public string ExpectedValue {
        get => _expectedValue;
        set => this.RaiseAndSetIfChanged(ref _expectedValue, value);
    }

    /// <summary>Gets or sets the domain name entered in the zone discovery tab.</summary>
    public string SearchDomain {
        get => _searchDomain;
        set => this.RaiseAndSetIfChanged(ref _searchDomain, value);
    }

    /// <summary>Gets or sets the status summary shown after zone discovery runs.</summary>
    public string DiscoverySummary {
        get => _discoverySummary;
        set => this.RaiseAndSetIfChanged(ref _discoverySummary, value);
    }

    /// <summary>Gets or sets the candidate hosts and DNS records found during zone discovery.</summary>
    public ObservableCollection<SelectableHost> ResolvedHosts {
        get => _resolvedHosts;
        set => this.RaiseAndSetIfChanged(ref _resolvedHosts, value);
    }

    /// <summary>Gets a value indicating whether DNS record-specific inputs are shown.</summary>
    public bool IsDnsRecordMode => SelectedCheckType == ExpectationType.DnsRecord;

    /// <summary>Gets a value indicating whether DNS or zone audit mode is selected.</summary>
    public bool IsDnsOrAuditMode => SelectedCheckType is ExpectationType.DnsRecord or ExpectationType.ZoneAudit;

    /// <summary>Gets a value indicating whether an HTTP endpoint check mode is selected.</summary>
    public bool IsHttpMode => !IsDnsOrAuditMode;

    /// <summary>Gets the button label displaying the number of configured expected headers.</summary>
    public string HeadersButtonLabel => $"Expected Headers ({_expectedHeaders.Count})...";

    /// <summary>Gets the button label displaying the number of configured miscellaneous DNS values.</summary>
    public string MiscDnsButtonLabel => $"Misc DNS Values ({_miscDnsValues.Count})...";

    /// <summary>Gets a contextual watermark hint for the expected value text box.</summary>
    public string ExpectedValueWatermark => SelectedCheckType switch {
        ExpectationType.DnsRecord when AllowDnsAbsent => "(Leave blank when Allow DNS Absent is checked)",
        ExpectationType.DnsRecord => "One record value per line (e.g. 104.21.13.27 or 0 issue \"letsencrypt.org\")",
        ExpectationType.ZoneAudit => "(Optional notes; audits live DNS against all registered entries in this main hostname)",
        ExpectationType.Redirect => "e.g., https://github.com/yggdrasil-au or https://anyserver.cloudflareaccess.com/...",
        ExpectationType.HtmlHash => "Normalized SHA-256 hex digest",
        _ => "e.g., 200 (or comma-separated status codes)"
    };

    /// <summary>Gets the command that validates and saves the current entry.</summary>
    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> SaveCommand { get; }

    /// <summary>Gets the command that executes zone discovery for <see cref="SearchDomain"/>.</summary>
    public ReactiveCommand<Unit, Unit> ResolveCommand { get; }

    /// <summary>Gets the interaction that opens the expected HTTP headers editor dialog.</summary>
    public Interaction<Dictionary<string, string>, Dictionary<string, string>?> OpenHeadersInteraction { get; } = new();

    /// <summary>Gets the interaction that opens the miscellaneous DNS values editor dialog.</summary>
    public Interaction<Dictionary<string, string>, Dictionary<string, string>?> OpenMiscDnsValuesInteraction { get; } = new();

    /// <summary>Gets the command that invokes <see cref="OpenHeadersInteraction"/>.</summary>
    public ReactiveCommand<Unit, Unit> OpenHeadersCommand { get; }

    /// <summary>Gets the command that invokes <see cref="OpenMiscDnsValuesInteraction"/>.</summary>
    public ReactiveCommand<Unit, Unit> OpenMiscDnsValuesCommand { get; }

    /// <summary>Gets the command that cancels editing without saving.</summary>
    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> CancelCommand { get; }

    /// <summary>Gets the command that confirms batch addition of checked discovery items.</summary>
    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> AddSelectedCommand { get; }

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Discovery Helpers :: START :: */

    private void PopulateFromDiscoveryDto(ZoneDiscoveryDto dto) {
        ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
            Name = $"[Zone Audit] {dto.MainHostname}",
            Url = dto.MainHostname,
            MainHostname = dto.MainHostname,
            EntryKind = MonitoredEntryKind.DnsEntry,
            Expectation = ExpectationType.ZoneAudit,
            StrictDnsCheck = true,
            MiscDnsValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["WildcardDetected"] = dto.WildcardDetected.ToString()
            }
        }));

        foreach (KeyValuePair<string, List<DnsRecordDto>> kvp in dto.ApexRecords.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
            string expectedLines = string.Join(Environment.NewLine, kvp.Value.Select(r => r.Value));
            ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
                Name = $"[DNS {kvp.Key}] {dto.MainHostname}",
                Url = dto.MainHostname,
                MainHostname = dto.MainHostname,
                EntryKind = MonitoredEntryKind.DnsEntry,
                Expectation = ExpectationType.DnsRecord,
                DnsRecordType = kvp.Key,
                StrictDnsCheck = true,
                ExpectedValue = expectedLines
            }));
        }

        ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
            Name = dto.MainHostname,
            Url = $"https://{dto.MainHostname}",
            MainHostname = dto.MainHostname,
            EntryKind = MonitoredEntryKind.Host,
            Expectation = ExpectationType.HttpStatus,
            ExpectedValue = "200"
        }));

        foreach (KeyValuePair<string, List<string>> sub in dto.DiscoveredSubdomains) {
            bool isServiceRecord = sub.Key.StartsWith('_') || sub.Key.Contains("._", StringComparison.Ordinal);
            ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
                Name = sub.Key,
                Url = isServiceRecord ? sub.Key : $"https://{sub.Key}",
                MainHostname = dto.MainHostname,
                EntryKind = isServiceRecord ? MonitoredEntryKind.DnsEntry : MonitoredEntryKind.Host,
                Expectation = isServiceRecord ? ExpectationType.DnsRecord : ExpectationType.HttpStatus,
                DnsRecordType = isServiceRecord ? "TXT" : null,
                ExpectedValue = isServiceRecord ? string.Join(Environment.NewLine, sub.Value) : "200"
            }));
        }

        DiscoverySummary = $"Discovered {dto.ApexRecords.Count} apex DNS type(s) and {dto.DiscoveredSubdomains.Count} subdomain(s). Wildcard={dto.WildcardDetected}, crt.sh={dto.CrtShStatus}";
    }

    private async System.Threading.Tasks.Task PopulateFallbackDiscoveryAsync(string search) {
        ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
            Name = search,
            Url = $"https://{search}",
            MainHostname = search,
            EntryKind = MonitoredEntryKind.Host,
            Expectation = ExpectationType.HttpStatus,
            ExpectedValue = "200"
        }));

        try {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "NetState-Client/1.0");

            string json = await client.GetStringAsync($"https://crt.sh/?q=%.{search}&output=json");
            using JsonDocument doc = JsonDocument.Parse(json);

            HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in doc.RootElement.EnumerateArray()) {
                if (element.TryGetProperty("name_value", out JsonElement nameVal)) {
                    string[]? names = nameVal.GetString()?.Split('\n');
                    if (names != null) {
                        foreach (string name in names) {
                            string cleanName = DomainGroupingHelper.NormalizeDnsName(name);
                            if (!cleanName.Contains('*') &&
                                cleanName.EndsWith(search, StringComparison.OrdinalIgnoreCase) &&
                                !cleanName.Equals(search, StringComparison.OrdinalIgnoreCase)) {
                                hosts.Add(cleanName);
                            }
                        }
                    }
                }
            }

            foreach (string host in hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase)) {
                ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
                    Name = host,
                    Url = $"https://{host}",
                    MainHostname = search,
                    EntryKind = MonitoredEntryKind.Host,
                    Expectation = ExpectationType.HttpStatus,
                    ExpectedValue = "200"
                }));
            }

            DiscoverySummary = $"Discovered {hosts.Count} hostname(s) via crt.sh fallback.";
        } catch (Exception ex) {
            DiscoverySummary = $"Fallback discovery note: {ex.Message}";
            Diagnostics.Log($"Failed to fetch subdomains from crt.sh: {ex.Message}");
        }
    }

    /* :: :: Discovery Helpers :: END :: */
}

/* :: :: AddDomainViewModel :: END :: */

using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using NetState.Shared.Models;
using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetState.Client.ViewModels;

public class SelectableHost : ReactiveObject {
    private bool _isSelected;
    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    
    public MonitoredDomain Domain { get; }
    
    public SelectableHost(MonitoredDomain domain) {
        Domain = domain;
        IsSelected = true;
    }
}

public class AddDomainViewModel : ReactiveObject {
    private MonitoredDomain? _existingDomain;
    private Dictionary<string, string> _expectedHeaders = new();

    private string _domainName = string.Empty;
    public string DomainName {
        get => _domainName;
        set => this.RaiseAndSetIfChanged(ref _domainName, value);
    }

    private string _url = string.Empty;
    public string Url {
        get => _url;
        set => this.RaiseAndSetIfChanged(ref _url, value);
    }

    private ExpectationType _selectedCheckType;
    public ExpectationType SelectedCheckType {
        get => _selectedCheckType;
        set => this.RaiseAndSetIfChanged(ref _selectedCheckType, value);
    }

    private string _expectedValue = string.Empty;
    public string ExpectedValue {
        get => _expectedValue;
        set => this.RaiseAndSetIfChanged(ref _expectedValue, value);
    }

    private string _searchDomain = string.Empty;
    public string SearchDomain {
        get => _searchDomain;
        set => this.RaiseAndSetIfChanged(ref _searchDomain, value);
    }

    private ObservableCollection<SelectableHost> _resolvedHosts = new();
    public ObservableCollection<SelectableHost> ResolvedHosts {
        get => _resolvedHosts;
        set => this.RaiseAndSetIfChanged(ref _resolvedHosts, value);
    }

    public ExpectationType[] AvailableCheckTypes => new[] {
        ExpectationType.HttpStatus,
        ExpectationType.Redirect,
        ExpectationType.HtmlHash
    };

    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand { get; }
    public Interaction<Dictionary<string, string>, Dictionary<string, string>?> OpenHeadersInteraction { get; } = new();
    public ReactiveCommand<Unit, Unit> OpenHeadersCommand { get; }
    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> CancelCommand { get; }
    public ReactiveCommand<Unit, IEnumerable<MonitoredDomain>?> AddSelectedCommand { get; }

    public AddDomainViewModel(MonitoredDomain? existing = null) {
        _existingDomain = existing;
        if (existing != null) {
            DomainName = existing.Name;
            Url = existing.Url;
            SelectedCheckType = existing.Expectation;
            ExpectedValue = existing.ExpectedValue ?? string.Empty;
            _expectedHeaders = existing.ExpectedHeaders ?? new();
        }

        var canSave = this.WhenAnyValue(
            x => x.DomainName,
            x => x.Url,
            x => x.ExpectedValue,
            (name, url, expected) =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(url) &&
                !string.IsNullOrWhiteSpace(expected)
        );

        SaveCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => {
            if (_existingDomain != null) {
                _existingDomain.Name = this.DomainName.Trim();
                _existingDomain.Url = this.Url.Trim();
                _existingDomain.Expectation = this.SelectedCheckType;
                _existingDomain.ExpectedValue = this.ExpectedValue.Trim();
                _existingDomain.ExpectedHeaders = _expectedHeaders;
                return new List<MonitoredDomain> { _existingDomain };
            }

            return new List<MonitoredDomain> { new MonitoredDomain {
                Name = this.DomainName.Trim(),
                Url = this.Url.Trim(),
                Expectation = this.SelectedCheckType,
                ExpectedValue = this.ExpectedValue.Trim(),
                ExpectedHeaders = _expectedHeaders
            }};
        }, canSave);

        AddSelectedCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => {
            var selected = ResolvedHosts.Where(h => h.IsSelected).Select(h => h.Domain).ToList();
            return selected.Count > 0 ? selected : null;
        });

        OpenHeadersCommand = ReactiveCommand.CreateFromTask(async () => {
            var result = await OpenHeadersInteraction.Handle(_expectedHeaders);
            if (result != null) {
                _expectedHeaders = result;
            }
        });

        CancelCommand = ReactiveCommand.Create<IEnumerable<MonitoredDomain>?>(() => null);

        ResolveCommand = ReactiveCommand.CreateFromTask(async () => {
            if (string.IsNullOrWhiteSpace(SearchDomain)) return;

            try {
                ResolvedHosts.Clear();
                var search = SearchDomain.Trim().ToLowerInvariant();

                // 1. Add the Base Domain
                ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
                    Name = search,
                    Url = $"https://{search}",
                    Expectation = ExpectationType.HttpStatus,
                    ExpectedValue = "200"
                }));

                // 2. Fetch Subdomains via Certificate Transparency Logs (crt.sh)
                try {
                    using var client = new HttpClient();
                    // crt.sh requires a User-Agent
                    client.DefaultRequestHeaders.Add("User-Agent", "NetState-Client/1.0");
                    
                    var json = await client.GetStringAsync($"https://crt.sh/?q=%.{search}&output=json");
                    using var doc = JsonDocument.Parse(json);
                    
                    var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Parse the JSON array
                    foreach (var element in doc.RootElement.EnumerateArray()) {
                        if (element.TryGetProperty("name_value", out var nameVal)) {
                            var names = nameVal.GetString()?.Split('\n');
                            if (names != null) {
                                foreach (var name in names) {
                                    var cleanName = name.Trim().ToLowerInvariant();
                                    
                                    // Exclude wildcard certs (*.yggdrasil.au) and ensure it's a subdomain
                                    if (!cleanName.Contains('*') && cleanName.EndsWith(search) && cleanName != search) {
                                        hosts.Add(cleanName);
                                    }
                                }
                            }
                        }
                    }

                    // Add discovered hosts alphabetically
                    foreach (var host in hosts.OrderBy(h => h)) {
                        ResolvedHosts.Add(new SelectableHost(new MonitoredDomain {
                            Name = host,
                            Url = $"https://{host}",
                            Expectation = ExpectationType.HttpStatus,
                            ExpectedValue = "200"
                        }));
                    }
                } catch (Exception ex) {
                    // Log failure, but the base domain will still be in the list
                    NetState.Shared.Core.Diagnostics.Log($"Failed to fetch subdomains from crt.sh: {ex.Message}");
                }
            } catch { }
        });
    }
}

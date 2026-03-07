using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using NetState.Shared.Models;
using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace NetState.Client.ViewModels;

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

    private ObservableCollection<MonitoredDomain> _resolvedHosts = new();
    public ObservableCollection<MonitoredDomain> ResolvedHosts {
        get => _resolvedHosts;
        set => this.RaiseAndSetIfChanged(ref _resolvedHosts, value);
    }

    public ExpectationType[] AvailableCheckTypes => new[] {
        ExpectationType.HttpStatus,
        ExpectationType.Redirect,
        ExpectationType.HtmlHash
    };

    // Returns a MonitoredDomain if saved, or null if canceled
    // Returns a MonitoredDomain if saved, or null if canceled
    public ReactiveCommand<Unit, MonitoredDomain?> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand { get; }
    public Interaction<Dictionary<string, string>, Dictionary<string, string>?> OpenHeadersInteraction { get; } = new();
    public ReactiveCommand<Unit, Unit> OpenHeadersCommand { get; }
    public ReactiveCommand<Unit, MonitoredDomain?> CancelCommand { get; }

    public AddDomainViewModel(MonitoredDomain? existing = null) {
        _existingDomain = existing;
        if (existing != null) {
            DomainName = existing.Name;
            Url = existing.Url;
            SelectedCheckType = existing.Expectation;
            ExpectedValue = existing.ExpectedValue ?? string.Empty;
            _expectedHeaders = existing.ExpectedHeaders ?? new();
        }

        // Validation: Only allow saving if Name, URL and Expected Value are filled out
        var canSave = this.WhenAnyValue(
            x => x.DomainName,
            x => x.Url,
            x => x.ExpectedValue,
            (name, url, expected) =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(url) &&
                !string.IsNullOrWhiteSpace(expected)
        );

        SaveCommand = ReactiveCommand.Create<MonitoredDomain?>(() => {
            if (_existingDomain != null) {
                _existingDomain.Name = this.DomainName.Trim();
                _existingDomain.Url = this.Url.Trim();
                _existingDomain.Expectation = this.SelectedCheckType;
                _existingDomain.ExpectedValue = this.ExpectedValue.Trim();
                _existingDomain.ExpectedHeaders = _expectedHeaders;
                return _existingDomain;
            }

            return new MonitoredDomain {
                Name = this.DomainName.Trim(),
                Url = this.Url.Trim(),
                Expectation = this.SelectedCheckType,
                ExpectedValue = this.ExpectedValue.Trim(),
                ExpectedHeaders = _expectedHeaders
            };
        }, canSave);

        OpenHeadersCommand = ReactiveCommand.CreateFromTask(async () => {
            var result = await OpenHeadersInteraction.Handle(_expectedHeaders);
            if (result != null) {
                _expectedHeaders = result;
            }
        });

        CancelCommand = ReactiveCommand.Create(() => (MonitoredDomain?)null);

        ResolveCommand = ReactiveCommand.CreateFromTask(async () => {
            if (string.IsNullOrWhiteSpace(SearchDomain)) return;

            try {
                var result = await Dns.GetHostEntryAsync(SearchDomain);
                ResolvedHosts.Clear();
                
                foreach (var ip in result.AddressList) {
                    ResolvedHosts.Add(new MonitoredDomain {
                        Name = $"{SearchDomain} ({ip})",
                        Url = $"http://{ip}",
                        Expectation = ExpectationType.HttpStatus,
                        ExpectedValue = "200"
                    });
                }

                if (!string.IsNullOrEmpty(result.HostName) && result.HostName != SearchDomain) {
                    ResolvedHosts.Add(new MonitoredDomain {
                        Name = $"{SearchDomain} (CNAME: {result.HostName})",
                        Url = $"http://{result.HostName}",
                        Expectation = ExpectationType.HttpStatus,
                        ExpectedValue = "200"
                    });
                }
            } catch { }
        });

        // Return null to indicate cancellation
        CancelCommand = ReactiveCommand.Create<MonitoredDomain?>(() => (MonitoredDomain?)null);
    }
}

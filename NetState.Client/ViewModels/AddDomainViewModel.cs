using ReactiveUI;
using System.Reactive;
using NetState.Shared.Models;

namespace NetState.Client.ViewModels;

public class AddDomainViewModel : ReactiveObject {
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

    public ExpectationType[] AvailableCheckTypes => new[] {
        ExpectationType.HttpStatus,
        ExpectationType.Redirect,
        ExpectationType.HtmlHash
    };

    // Returns a MonitoredDomain if saved, or null if canceled
    public ReactiveCommand<Unit, MonitoredDomain?> SaveCommand { get; }
    public ReactiveCommand<Unit, MonitoredDomain?> CancelCommand { get; }

    public AddDomainViewModel() {
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
            return new MonitoredDomain {
                Name = this.DomainName.Trim(),
                Url = this.Url.Trim(),
                Expectation = this.SelectedCheckType,
                ExpectedValue = this.ExpectedValue.Trim()
            };
        }, canSave);

        // Return null to indicate cancellation
        CancelCommand = ReactiveCommand.Create<MonitoredDomain?>(() => (MonitoredDomain?)null);
    }
}

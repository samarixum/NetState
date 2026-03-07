using ReactiveUI;
using System.Reactive;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace NetState.Client.ViewModels;

public class HeaderEntry : ReactiveObject {
    public string Key { get; }
    public string Value { get; }

    public HeaderEntry(string key, string value) {
        Key = key;
        Value = value;
    }
}

public class ExpectedHeadersViewModel : ReactiveObject {
    private string _newHeaderKey = string.Empty;
    public string NewHeaderKey {
        get => _newHeaderKey;
        set => this.RaiseAndSetIfChanged(ref _newHeaderKey, value);
    }

    private string _newHeaderValue = string.Empty;
    public string NewHeaderValue {
        get => _newHeaderValue;
        set => this.RaiseAndSetIfChanged(ref _newHeaderValue, value);
    }

    public ObservableCollection<HeaderEntry> Headers { get; } = new();

    public ReactiveCommand<Unit, Unit> AddHeaderCommand { get; }
    public ReactiveCommand<HeaderEntry, Unit> RemoveHeaderCommand { get; }
    public ReactiveCommand<Unit, Dictionary<string, string>> SaveHeadersCommand { get; }

    public ExpectedHeadersViewModel(Dictionary<string, string>? initial = null) {
        if (initial != null) {
            foreach (var kvp in initial) {
                Headers.Add(new HeaderEntry(kvp.Key, kvp.Value));
            }
        }

        AddHeaderCommand = ReactiveCommand.Create(() => {
            if (!string.IsNullOrWhiteSpace(NewHeaderKey) && !string.IsNullOrWhiteSpace(NewHeaderValue)) {
                Headers.Add(new HeaderEntry(NewHeaderKey.Trim(), NewHeaderValue.Trim()));
                NewHeaderKey = string.Empty;
                NewHeaderValue = string.Empty;
            }
        });

        RemoveHeaderCommand = ReactiveCommand.Create<HeaderEntry>(entry => {
            Headers.Remove(entry);
        });

        SaveHeadersCommand = ReactiveCommand.Create(() => {
            return Headers.ToDictionary(h => h.Key, h => h.Value);
        });
    }
}

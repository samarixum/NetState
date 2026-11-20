using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace NetState.Client.ViewModels;

/* :: :: MiscDnsValues Models :: START :: */

/// <summary>
/// Represents a single editable key-value entry in the miscellaneous DNS values editor.
/// </summary>
public class MiscDnsEntry : ReactiveObject {
    private string _key;
    private string _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="MiscDnsEntry"/> class.
    /// </summary>
    /// <param name="key">The metadata or assertion key.</param>
    /// <param name="value">The associated value.</param>
    public MiscDnsEntry(string key, string value) {
        _key = key;
        _value = value;
    }

    /// <summary>
    /// Gets or sets the entry key.
    /// </summary>
    public string Key {
        get => _key;
        set => this.RaiseAndSetIfChanged(ref _key, value);
    }

    /// <summary>
    /// Gets or sets the entry value.
    /// </summary>
    public string Value {
        get => _value;
        set => this.RaiseAndSetIfChanged(ref _value, value);
    }
}

/// <summary>
/// View model for editing miscellaneous DNS metadata and optional custom DNS assertion keys.
/// </summary>
public class MiscDnsValuesViewModel : ReactiveObject {
    private string _newKey = string.Empty;
    private string _newValue = string.Empty;
    private string? _selectedPresetKey;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="MiscDnsValuesViewModel"/> class.
    /// </summary>
    /// <param name="initial">Existing miscellaneous DNS key-value pairs to edit.</param>
    public MiscDnsValuesViewModel(Dictionary<string, string>? initial = null) {
        if (initial != null) {
            foreach (KeyValuePair<string, string> kvp in initial.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                Entries.Add(new MiscDnsEntry(kvp.Key, kvp.Value));
            }
        }

        AddEntryCommand = ReactiveCommand.Create(() => {
            if (string.IsNullOrWhiteSpace(NewKey) || string.IsNullOrWhiteSpace(NewValue)) {
                return;
            }

            string cleanKey = NewKey.Trim();
            string cleanVal = NewValue.Trim();

            MiscDnsEntry? existing = Entries.FirstOrDefault(e => string.Equals(e.Key, cleanKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                existing.Value = cleanVal;
            } else {
                Entries.Add(new MiscDnsEntry(cleanKey, cleanVal));
            }

            NewKey = string.Empty;
            NewValue = string.Empty;
            SelectedPresetKey = null;
        });

        RemoveEntryCommand = ReactiveCommand.Create<MiscDnsEntry>(entry => {
            if (entry != null) {
                Entries.Remove(entry);
            }
        });

        SaveCommand = ReactiveCommand.Create<Dictionary<string, string>?>(() => {
            Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
            foreach (MiscDnsEntry entry in Entries) {
                if (!string.IsNullOrWhiteSpace(entry.Key)) {
                    dict[entry.Key.Trim()] = entry.Value?.Trim() ?? string.Empty;
                }
            }
            return dict;
        });

        CancelCommand = ReactiveCommand.Create<Dictionary<string, string>?>(() => null);
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    /// <summary>
    /// Common metadata and custom DNS validation directive key presets.
    /// </summary>
    public string[] PresetKeys { get; } = {
        "ProxyStatus",
        "CloudflareType",
        "TunnelName",
        "TunnelId",
        "WorkerService",
        "R2Bucket",
        "AccessType",
        "AccessAppName",
        "Notes",
        "AssertContains",
        "AssertMaxTtl",
        "AssertDnsServer",
        "AssertNoOtherRecordTypes",
        "IgnoreSoaSerial",
        "EnableCrtSh",
        "AssertNotWildcard",
        "IgnoreSubdomains"
    };

    /// <summary>
    /// Gets or sets the selected preset key, which automatically populates <see cref="NewKey"/>.
    /// </summary>
    public string? SelectedPresetKey {
        get => _selectedPresetKey;
        set {
            this.RaiseAndSetIfChanged(ref _selectedPresetKey, value);
            if (!string.IsNullOrWhiteSpace(value)) {
                NewKey = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the key for a new or updated entry.
    /// </summary>
    public string NewKey {
        get => _newKey;
        set => this.RaiseAndSetIfChanged(ref _newKey, value);
    }

    /// <summary>
    /// Gets or sets the value for a new or updated entry.
    /// </summary>
    public string NewValue {
        get => _newValue;
        set => this.RaiseAndSetIfChanged(ref _newValue, value);
    }

    /// <summary>
    /// Gets the collection of editable key-value entries.
    /// </summary>
    public ObservableCollection<MiscDnsEntry> Entries { get; } = new();

    /// <summary>Gets the command that adds or updates a key-value pair in the list.</summary>
    public ReactiveCommand<Unit, Unit> AddEntryCommand { get; }

    /// <summary>Gets the command that removes an entry from the list.</summary>
    public ReactiveCommand<MiscDnsEntry, Unit> RemoveEntryCommand { get; }

    /// <summary>Gets the command that returns the edited misc DNS values dictionary and closes the dialog.</summary>
    public ReactiveCommand<Unit, Dictionary<string, string>?> SaveCommand { get; }

    /// <summary>Gets the command that discards changes and closes the dialog.</summary>
    public ReactiveCommand<Unit, Dictionary<string, string>?> CancelCommand { get; }

    /* :: :: Properties :: END :: */
}

/* :: :: MiscDnsValues Models :: END :: */

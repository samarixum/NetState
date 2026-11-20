using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using NetState.Client.Services;
using NetState.Client.Views;
using NetState.Shared.Core;
using NetState.Shared.Models;
using ReactiveUI;

namespace NetState.Client.ViewModels;

/* :: :: MainHostnameGroupViewModel :: START :: */

/// <summary>
/// Represents a grouped view for a single main hostname, placing DNS entries directly under the group
/// and placing all hosts within an expandable dropdown section.
/// </summary>
public class MainHostnameGroupViewModel : ReactiveObject {
    private readonly Action<string, bool>? _onExpansionChanged;
    private bool _isHostsExpanded;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="MainHostnameGroupViewModel"/> class.
    /// </summary>
    /// <param name="mainHostname">The normalized main hostname for this group.</param>
    /// <param name="dnsEntries">The DNS record and zone audit entries under this main hostname.</param>
    /// <param name="hostEntries">The host entries displayed in the hosts dropdown under this main hostname.</param>
    /// <param name="isHostsExpanded">Whether the hosts dropdown starts expanded.</param>
    /// <param name="onExpansionChanged">Optional callback notified when the user toggles the hosts dropdown.</param>
    public MainHostnameGroupViewModel(
        string mainHostname,
        IEnumerable<MonitoredDomain> dnsEntries,
        IEnumerable<MonitoredDomain> hostEntries,
        bool isHostsExpanded = false,
        Action<string, bool>? onExpansionChanged = null
    ) {
        MainHostname = mainHostname;
        DnsEntries = new ObservableCollection<MonitoredDomain>(dnsEntries);
        HostEntries = new ObservableCollection<MonitoredDomain>(hostEntries);
        _isHostsExpanded = isHostsExpanded;
        _onExpansionChanged = onExpansionChanged;

        List<MonitoredDomain> all = DnsEntries.Concat(HostEntries).ToList();
        AggregateStatus = ComputeAggregateStatus(all);

        int healthy = all.Count(d => d.LastStatus == CheckStatus.Healthy);
        int degraded = all.Count(d => d.LastStatus == CheckStatus.Degraded);
        int down = all.Count(d => d.LastStatus == CheckStatus.Down);
        int unknown = all.Count(d => d.LastStatus == CheckStatus.Unknown);

        List<string> stats = new() {
            $"{DnsEntries.Count} DNS",
            $"{HostEntries.Count} Hosts"
        };
        if (healthy > 0) { stats.Add($"{healthy} Healthy"); }
        if (degraded > 0) { stats.Add($"{degraded} Degraded"); }
        if (down > 0) { stats.Add($"{down} Down"); }
        if (unknown > 0) { stats.Add($"{unknown} Unknown"); }

        SummaryText = string.Join(" · ", stats);
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    /// <summary>
    /// Gets the main hostname heading for this group.
    /// </summary>
    public string MainHostname { get; }

    /// <summary>
    /// Gets the DNS entries and zone audits displayed directly under <see cref="MainHostname"/>.
    /// </summary>
    public ObservableCollection<MonitoredDomain> DnsEntries { get; }

    /// <summary>
    /// Gets the hosts displayed inside the dropdown expander under <see cref="MainHostname"/>.
    /// </summary>
    public ObservableCollection<MonitoredDomain> HostEntries { get; }

    /// <summary>
    /// Gets or sets whether the hosts dropdown under this main hostname is expanded.
    /// </summary>
    public bool IsHostsExpanded {
        get => _isHostsExpanded;
        set {
            if (_isHostsExpanded != value) {
                this.RaiseAndSetIfChanged(ref _isHostsExpanded, value);
                _onExpansionChanged?.Invoke(MainHostname, value);
            }
        }
    }

    /// <summary>
    /// Gets the rolled-up health status across DNS entries and hosts in this group.
    /// </summary>
    public CheckStatus AggregateStatus { get; }

    /// <summary>
    /// Gets the compact group statistics label.
    /// </summary>
    public string SummaryText { get; }

    /// <summary>
    /// Gets the header label for the DNS entries section directly under the main hostname.
    /// </summary>
    public string DnsSectionHeader => $"DNS Entries & Zone Audit ({DnsEntries.Count})";

    /// <summary>
    /// Gets the header label for the expandable hosts dropdown under the main hostname.
    /// </summary>
    public string HostsDropdownHeader => $"Hosts Dropdown ({HostEntries.Count})";

    /// <summary>
    /// Gets a value indicating whether any DNS entries exist in this group.
    /// </summary>
    public bool HasDnsEntries => DnsEntries.Count > 0;

    /// <summary>
    /// Gets a value indicating whether any host entries exist in this group.
    /// </summary>
    public bool HasHostEntries => HostEntries.Count > 0;

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Helpers :: START :: */

    private static CheckStatus ComputeAggregateStatus(IReadOnlyList<MonitoredDomain> entries) {
        if (entries.Count == 0) {
            return CheckStatus.Unknown;
        }

        if (entries.Any(d => d.LastStatus == CheckStatus.Down)) {
            return CheckStatus.Down;
        }

        if (entries.Any(d => d.LastStatus == CheckStatus.Degraded)) {
            return CheckStatus.Degraded;
        }

        if (entries.Any(d => d.LastStatus == CheckStatus.Healthy)) {
            return CheckStatus.Healthy;
        }

        return CheckStatus.Unknown;
    }

    /* :: :: Helpers :: END :: */
}

/* :: :: MainHostnameGroupViewModel :: END :: */
// //
/* :: :: MainWindowViewModel :: START :: */

/// <summary>
/// Primary dashboard view model organizing monitored DNS entries and hosts by main hostname.
/// </summary>
public class MainWindowViewModel : ReactiveObject {
    private readonly MonitoringApiClient? _apiClient;
    private readonly Dictionary<string, bool> _hostsExpandedState = new(StringComparer.OrdinalIgnoreCase);
    private List<MonitoredDomain> _allDomains = new();

    private ObservableCollection<MonitoredDomain> _domains = new();
    private ObservableCollection<MainHostnameGroupViewModel> _mainHostnameGroups = new();
    private string _searchQuery = string.Empty;
    private string _globalSummaryText = string.Empty;
    private bool _isBusy;
    private bool _isConnected;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class for runtime execution.
    /// </summary>
    public MainWindowViewModel() : this(new MonitoringApiClient("http://localhost:5138/"), startAutoRefresh: true) {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class with optional API client and auto-refresh control.
    /// </summary>
    /// <param name="apiClient">The backend API client, or null in offline unit tests.</param>
    /// <param name="startAutoRefresh">True to enable the 30-second Avalonia timer and initial load.</param>
    public MainWindowViewModel(MonitoringApiClient? apiClient, bool startAutoRefresh = false) {
        _apiClient = apiClient;

        LoadDomainsCommand = ReactiveCommand.CreateFromTask(LoadDomainsAsync);
        AddDomainCommand = ReactiveCommand.CreateFromTask(AddDomainAsync);
        AddDnsForGroupCommand = ReactiveCommand.CreateFromTask<string>(AddDnsForGroupAsync);
        AddHostForGroupCommand = ReactiveCommand.CreateFromTask<string>(AddHostForGroupAsync);
        EditDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(EditDomainAsync);
        DeleteDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(DeleteDomainAsync);
        CheckDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(CheckDomainAsync);
        CheckAllCommand = ReactiveCommand.CreateFromTask(CheckAllAsync);
        CheckGroupCommand = ReactiveCommand.CreateFromTask<string>(CheckGroupAsync);
        InspectDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(InspectDomainAsync);
        ExpandAllCommand = ReactiveCommand.Create(ExpandAllGroups);
        CollapseAllCommand = ReactiveCommand.Create(CollapseAllGroups);

        if (startAutoRefresh) {
            Observable.Interval(TimeSpan.FromSeconds(30))
                .Select(_ => Unit.Default)
                .ObserveOn(Avalonia.ReactiveUI.AvaloniaScheduler.Instance)
                .InvokeCommand(LoadDomainsCommand);

            LoadDomainsCommand.Execute().Subscribe(_ => { });
        }
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    /// <summary>
    /// Gets or sets the flat list of monitored domains loaded from the server.
    /// </summary>
    public ObservableCollection<MonitoredDomain> Domains {
        get => _domains;
        set => this.RaiseAndSetIfChanged(ref _domains, value);
    }

    /// <summary>
    /// Gets or sets the grouped main hostname cards containing DNS entries and hosts dropdowns.
    /// </summary>
    public ObservableCollection<MainHostnameGroupViewModel> MainHostnameGroups {
        get => _mainHostnameGroups;
        set => this.RaiseAndSetIfChanged(ref _mainHostnameGroups, value);
    }

    /// <summary>
    /// Gets or sets the search filter applied across entry names, URLs, expected values, and main hostnames.
    /// </summary>
    public string SearchQuery {
        get => _searchQuery;
        set {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            RebuildGroups();
        }
    }

    /// <summary>
    /// Gets or sets the global summary counts across all loaded entries.
    /// </summary>
    public string GlobalSummaryText {
        get => _globalSummaryText;
        set => this.RaiseAndSetIfChanged(ref _globalSummaryText, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether a dashboard API operation is currently running.
    /// </summary>
    public bool IsBusy {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the client is connected to NetState.Server.
    /// </summary>
    public bool IsConnected {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>Gets the command that reloads all monitored entries from the server.</summary>
    public ReactiveCommand<Unit, Unit> LoadDomainsCommand { get; }

    /// <summary>Gets the command that opens the dialog to add a new monitored entry.</summary>
    public ReactiveCommand<Unit, Unit> AddDomainCommand { get; }

    /// <summary>Gets the command that opens the add dialog pre-configured for a DNS entry in the specified main hostname group.</summary>
    public ReactiveCommand<string, Unit> AddDnsForGroupCommand { get; }

    /// <summary>Gets the command that opens the add dialog pre-configured for a host entry in the specified main hostname group.</summary>
    public ReactiveCommand<string, Unit> AddHostForGroupCommand { get; }

    /// <summary>Gets the command that opens the edit dialog for a monitored entry.</summary>
    public ReactiveCommand<MonitoredDomain, Unit> EditDomainCommand { get; }

    /// <summary>Gets the command that deletes a monitored entry.</summary>
    public ReactiveCommand<MonitoredDomain, Unit> DeleteDomainCommand { get; }

    /// <summary>Gets the command that triggers an immediate check for a single monitored entry.</summary>
    public ReactiveCommand<MonitoredDomain, Unit> CheckDomainCommand { get; }

    /// <summary>Gets the command that triggers an immediate parallel check across all monitored entries.</summary>
    public ReactiveCommand<Unit, Unit> CheckAllCommand { get; }

    /// <summary>Gets the command that triggers an immediate parallel check for all entries in a main hostname group.</summary>
    public ReactiveCommand<string, Unit> CheckGroupCommand { get; }

    /// <summary>Gets the command that opens the inspection dialog for a monitored entry.</summary>
    public ReactiveCommand<MonitoredDomain, Unit> InspectDomainCommand { get; }

    /// <summary>Gets the command that expands all main hostname host dropdowns.</summary>
    public ReactiveCommand<Unit, Unit> ExpandAllCommand { get; }

    /// <summary>Gets the command that collapses all main hostname host dropdowns.</summary>
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Grouping & Filtering :: START :: */

    /// <summary>
    /// Replaces the in-memory dataset of monitored entries and rebuilds grouped main hostname views
    /// while preserving expanded/collapsed state for host dropdowns.
    /// </summary>
    /// <param name="domains">The updated list of monitored entries.</param>
    public void ApplyDomains(IEnumerable<MonitoredDomain> domains) {
        _allDomains = domains?.ToList() ?? new List<MonitoredDomain>();
        Domains = new ObservableCollection<MonitoredDomain>(_allDomains);
        RebuildGroups();
    }

    private void RebuildGroups() {
        List<string> knownMains = _allDomains
            .Where(d => !string.IsNullOrWhiteSpace(d.MainHostname))
            .Select(d => DomainGroupingHelper.ExtractHost(d.MainHostname))
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string filter = SearchQuery?.Trim() ?? string.Empty;
        IEnumerable<MonitoredDomain> filtered = _allDomains;
        if (!string.IsNullOrEmpty(filter)) {
            filtered = _allDomains.Where(d => MatchesFilter(d, knownMains, filter));
        }

        List<MainHostnameGroupViewModel> groups = filtered
            .GroupBy(d => DomainGroupingHelper.ResolveMainHostname(d, knownMains), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => {
                string mainHost = g.Key;
                List<MonitoredDomain> items = g.ToList();

                List<MonitoredDomain> dnsItems = items
                    .Where(d => d.EntryKind == MonitoredEntryKind.DnsEntry || d.Expectation is ExpectationType.DnsRecord or ExpectationType.ZoneAudit)
                    .OrderBy(d => d.Expectation == ExpectationType.ZoneAudit ? 0 : 1)
                    .ThenBy(d => string.Equals(DomainGroupingHelper.ExtractHost(d.Url), mainHost, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(d => DomainGroupingHelper.ExtractHost(d.Url), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.DnsRecordType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                List<MonitoredDomain> hostItems = items
                    .Where(d => d.EntryKind == MonitoredEntryKind.Host && d.Expectation is not (ExpectationType.DnsRecord or ExpectationType.ZoneAudit))
                    .OrderBy(d => DomainGroupingHelper.ExtractHost(d.Url), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool expanded = _hostsExpandedState.TryGetValue(mainHost, out bool savedState) && savedState;
                return new MainHostnameGroupViewModel(
                    mainHostname: mainHost,
                    dnsEntries: dnsItems,
                    hostEntries: hostItems,
                    isHostsExpanded: expanded,
                    onExpansionChanged: OnGroupExpansionChanged
                );
            })
            .ToList();

        MainHostnameGroups = new ObservableCollection<MainHostnameGroupViewModel>(groups);

        int total = _allDomains.Count;
        int healthy = _allDomains.Count(d => d.LastStatus == CheckStatus.Healthy);
        int degraded = _allDomains.Count(d => d.LastStatus == CheckStatus.Degraded);
        int down = _allDomains.Count(d => d.LastStatus == CheckStatus.Down);
        GlobalSummaryText = $"Total: {total}  |  Healthy: {healthy}  |  Degraded: {degraded}  |  Down: {down}";
    }

    private void OnGroupExpansionChanged(string mainHostname, bool expanded) {
        _hostsExpandedState[mainHostname] = expanded;
    }

    private void ExpandAllGroups() {
        foreach (MainHostnameGroupViewModel group in MainHostnameGroups) {
            group.IsHostsExpanded = true;
        }
    }

    private void CollapseAllGroups() {
        foreach (MainHostnameGroupViewModel group in MainHostnameGroups) {
            group.IsHostsExpanded = false;
        }
    }

    private static bool MatchesFilter(MonitoredDomain domain, List<string> knownMains, string filter) {
        if (domain.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            domain.Url.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            DomainGroupingHelper.ResolveMainHostname(domain, knownMains).Contains(filter, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(domain.DnsRecordType) && domain.DnsRecordType.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(domain.ExpectedValue) && domain.ExpectedValue.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (domain.MiscDnsValues != null) {
            foreach (KeyValuePair<string, string> kvp in domain.MiscDnsValues) {
                if (kvp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }

        return false;
    }

    /* :: :: Grouping & Filtering :: END :: */
    // //
    /* :: :: API Command Handlers :: START :: */

    private async Task LoadDomainsAsync() {
        if (_apiClient == null) {
            return;
        }

        IsBusy = true;
        try {
            List<MonitoredDomain> domains = await _apiClient.GetDomainsAsync();
            ApplyDomains(domains);
            IsConnected = true;
        } catch (Exception ex) {
            IsConnected = false;
            Diagnostics.Bug("LoadDomainsAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private Task AddDomainAsync() {
        return OpenAddDialogAsync(defaultMainHostname: null, defaultEntryKind: null);
    }

    private Task AddDnsForGroupAsync(string mainHostname) {
        return OpenAddDialogAsync(defaultMainHostname: mainHostname, defaultEntryKind: MonitoredEntryKind.DnsEntry);
    }

    private Task AddHostForGroupAsync(string mainHostname) {
        return OpenAddDialogAsync(defaultMainHostname: mainHostname, defaultEntryKind: MonitoredEntryKind.Host);
    }

    private async Task OpenAddDialogAsync(string? defaultMainHostname, MonitoredEntryKind? defaultEntryKind) {
        if (_apiClient == null) {
            return;
        }

        AddDomainWindow dialog = new() {
            ViewModel = new AddDomainViewModel(
                existing: null,
                defaultMainHostname: defaultMainHostname,
                defaultEntryKind: defaultEntryKind,
                apiClient: _apiClient
            )
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
            IEnumerable<MonitoredDomain>? result = await dialog.ShowDialog<IEnumerable<MonitoredDomain>?>(desktop.MainWindow);
            if (result != null) {
                IsBusy = true;
                try {
                    foreach (MonitoredDomain domain in result) {
                        await _apiClient.CreateDomainAsync(domain);
                    }
                    await LoadDomainsAsync();
                } catch (Exception ex) {
                    Diagnostics.Bug("AddDomainAsync error", ex);
                } finally {
                    IsBusy = false;
                }
            }
        }
    }

    private async Task EditDomainAsync(MonitoredDomain domain) {
        if (domain == null || _apiClient == null) {
            return;
        }

        AddDomainWindow dialog = new() {
            ViewModel = new AddDomainViewModel(existing: domain, apiClient: _apiClient)
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
            IEnumerable<MonitoredDomain>? result = await dialog.ShowDialog<IEnumerable<MonitoredDomain>?>(desktop.MainWindow);
            if (result != null) {
                IsBusy = true;
                try {
                    foreach (MonitoredDomain updatedDomain in result) {
                        await _apiClient.UpdateDomainAsync(updatedDomain);
                        break;
                    }
                    await LoadDomainsAsync();
                } catch (Exception ex) {
                    Diagnostics.Bug("EditDomainAsync error", ex);
                } finally {
                    IsBusy = false;
                }
            }
        }
    }

    private async Task InspectDomainAsync(MonitoredDomain domain) {
        if (domain == null) {
            return;
        }

        InspectDomainWindow dialog = new() {
            ViewModel = new InspectDomainViewModel(domain)
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    private async Task DeleteDomainAsync(MonitoredDomain domain) {
        if (domain == null || _apiClient == null) {
            return;
        }

        IsBusy = true;
        try {
            await _apiClient.DeleteDomainAsync(domain.Id);
            _allDomains.RemoveAll(d => d.Id == domain.Id);
            ApplyDomains(_allDomains);
        } catch (Exception ex) {
            Diagnostics.Bug("DeleteDomainAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private async Task CheckDomainAsync(MonitoredDomain domain) {
        if (domain == null || _apiClient == null) {
            return;
        }

        IsBusy = true;
        try {
            await _apiClient.CheckDomainAsync(domain.Id);
            await LoadDomainsAsync();
        } catch (Exception ex) {
            Diagnostics.Bug("CheckDomainAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private async Task CheckAllAsync() {
        if (_apiClient == null) {
            return;
        }

        IsBusy = true;
        try {
            await _apiClient.CheckAllDomainsAsync();
            await LoadDomainsAsync();
        } catch (Exception ex) {
            Diagnostics.Bug("CheckAllAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private async Task CheckGroupAsync(string mainHostname) {
        if (string.IsNullOrWhiteSpace(mainHostname) || _apiClient == null) {
            return;
        }

        IsBusy = true;
        try {
            await _apiClient.CheckGroupAsync(mainHostname);
            await LoadDomainsAsync();
        } catch (Exception ex) {
            Diagnostics.Bug("CheckGroupAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    /* :: :: API Command Handlers :: END :: */
}

/* :: :: MainWindowViewModel :: END :: */

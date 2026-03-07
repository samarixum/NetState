namespace NetState.Client.ViewModels;

using System;
using System.Reactive;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NetState.Shared.Core;
using NetState.Shared.Models;
using NetState.Client.Services;
using NetState.Client.Views;
using ReactiveUI;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

public class MainWindowViewModel : ReactiveObject {
    private readonly MonitoringApiClient _apiClient;
    private ObservableCollection<MonitoredDomain> _domains = new();
    private bool _isBusy;
    private bool _isConnected;

    /* :: :: Constructors :: START :: */

    public MainWindowViewModel() {
        _apiClient = new MonitoringApiClient("http://localhost:5138/");
        LoadDomainsCommand = ReactiveCommand.CreateFromTask(LoadDomainsAsync);
        AddDomainCommand = ReactiveCommand.CreateFromTask(AddDomainAsync);
        EditDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(EditDomainAsync);
        DeleteDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(DeleteDomainAsync);
        CheckDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(CheckDomainAsync);
        InspectDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(InspectDomainAsync);

        // Auto-refresh every 30 seconds
        Observable.Interval(System.TimeSpan.FromSeconds(30))
            .Select(_ => Unit.Default)
            .ObserveOn(Avalonia.ReactiveUI.AvaloniaScheduler.Instance)
            .InvokeCommand(LoadDomainsCommand);

        LoadDomainsCommand.Execute().Subscribe(_ => { });
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Properties :: START :: */

    public ObservableCollection<MonitoredDomain> Domains {
        get => _domains;
        set => this.RaiseAndSetIfChanged(ref _domains, value);
    }

    public bool IsBusy {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool IsConnected {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadDomainsCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AddDomainCommand { get; }
    public ReactiveCommand<MonitoredDomain, System.Reactive.Unit> EditDomainCommand { get; }
    public ReactiveCommand<MonitoredDomain, System.Reactive.Unit> DeleteDomainCommand { get; }
    public ReactiveCommand<MonitoredDomain, System.Reactive.Unit> CheckDomainCommand { get; }
    public ReactiveCommand<MonitoredDomain, System.Reactive.Unit> InspectDomainCommand { get; }

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Methods :: START :: */

    private async Task LoadDomainsAsync() {
        IsBusy = true;
        try {
            var domains = await _apiClient.GetDomainsAsync();
            Domains = new ObservableCollection<MonitoredDomain>(domains);
            IsConnected = true;
        } catch (Exception ex) {
            IsConnected = false;
            Diagnostics.Bug("LoadDomainsAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private async Task AddDomainAsync() {
        var dialog = new AddDomainWindow { ViewModel = new AddDomainViewModel() };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var result = await dialog.ShowDialog<MonitoredDomain?>(desktop.MainWindow!);
            if (result != null) {
                IsBusy = true;
                try {
                    await _apiClient.CreateDomainAsync(result);
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
        if (domain == null) return;
        var dialog = new AddDomainWindow { ViewModel = new AddDomainViewModel(domain) };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var result = await dialog.ShowDialog<MonitoredDomain?>(desktop.MainWindow!);
            if (result != null) {
                IsBusy = true;
                try {
                    await _apiClient.UpdateDomainAsync(result);
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
        if (domain == null) return;
        var dialog = new InspectDomainWindow { ViewModel = new InspectDomainViewModel(domain) };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            await dialog.ShowDialog(desktop.MainWindow!);
        }
    }

    private async Task DeleteDomainAsync(MonitoredDomain domain) {
        if (domain == null) {
            return;
        }

        IsBusy = true;
        try {
            await _apiClient.DeleteDomainAsync(domain.Id);
            Domains.Remove(domain);
        } catch (Exception ex) {
            Diagnostics.Bug("DeleteDomainAsync error", ex);
        } finally {
            IsBusy = false;
        }
    }

    private async Task CheckDomainAsync(MonitoredDomain domain) {
        if (domain == null) {
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

    /* :: :: Methods :: END :: */
}

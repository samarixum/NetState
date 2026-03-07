namespace NetState.Client.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading.Tasks;
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

    /* :: :: Constructors :: START :: */

    public MainWindowViewModel() {
        _apiClient = new MonitoringApiClient("http://localhost:5037/"); 
        LoadDomainsCommand = ReactiveCommand.CreateFromTask(LoadDomainsAsync);
        AddDomainCommand = ReactiveCommand.CreateFromTask(AddDomainAsync);
        DeleteDomainCommand = ReactiveCommand.CreateFromTask<MonitoredDomain>(DeleteDomainAsync);
        
        // Auto-refresh every 30 seconds
        Observable.Interval(System.TimeSpan.FromSeconds(30))
            .ObserveOn(RxApp.MainThreadScheduler)
            .InvokeCommand(LoadDomainsCommand);

        LoadDomainsCommand.Execute().Subscribe();
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

    public IReactiveCommand LoadDomainsCommand { get; }
    public IReactiveCommand AddDomainCommand { get; }
    public IReactiveCommand DeleteDomainCommand { get; }

    /* :: :: Properties :: END :: */
    // //
    /* :: :: Methods :: START :: */

    private async Task LoadDomainsAsync() {
        IsBusy = true;
        try {
            var domains = await _apiClient.GetDomainsAsync();
            Domains = new ObservableCollection<MonitoredDomain>(domains);
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
                } finally {
                    IsBusy = false;
                }
            }
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
        } finally {
            IsBusy = false;
        }
    }

    /* :: :: Methods :: END :: */
}

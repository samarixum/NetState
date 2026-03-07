using ReactiveUI;
using NetState.Shared.Models;
using System.Collections.Generic;
using System.Linq;

namespace NetState.Client.ViewModels;

public class InspectDomainViewModel : ReactiveObject
{
    private MonitoredDomain _domain;
    public MonitoredDomain Domain
    {
        get => _domain;
        set => this.RaiseAndSetIfChanged(ref _domain, value);
    }

    public string ResponseBody => Domain.LastResponseBody ?? "No response body captured.";

    public List<KeyValuePair<string, string>> Headers => 
        Domain.LastResponseHeaders?.ToList() ?? new List<KeyValuePair<string, string>>();

    public InspectDomainViewModel(MonitoredDomain domain)
    {
        _domain = domain;
    }
}
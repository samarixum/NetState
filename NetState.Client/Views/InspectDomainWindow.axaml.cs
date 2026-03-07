using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;

namespace NetState.Client.Views;

public partial class InspectDomainWindow : ReactiveWindow<InspectDomainViewModel> {
    public InspectDomainWindow() {
        InitializeComponent();
    }
}
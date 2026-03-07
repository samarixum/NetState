using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;
using ReactiveUI;
using System;

namespace NetState.Client.Views;

public partial class ExpectedHeadersWindow : ReactiveWindow<ExpectedHeadersViewModel>
{
    public ExpectedHeadersWindow()
    {
        InitializeComponent();
        
        this.WhenActivated(d => {
            if (ViewModel != null) {
                d(ViewModel.SaveHeadersCommand.Subscribe(Close));
            }
        });
    }
}

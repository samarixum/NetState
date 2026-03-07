using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;
using NetState.Shared.Models;
using ReactiveUI;
using System;

namespace NetState.Client.Views;

public partial class AddDomainWindow : ReactiveWindow<AddDomainViewModel> {
    public AddDomainWindow() {
        InitializeComponent();

        // When Save or Cancel is clicked, close the window and return the result
        this.WhenActivated(action => {
            if (ViewModel != null) {
                action(ViewModel.SaveCommand.Subscribe(Close));
                action(ViewModel.CancelCommand.Subscribe(Close));
            }
        });
    }
}

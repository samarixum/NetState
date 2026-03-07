using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;
using NetState.Shared.Models;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetState.Client.Views;

public partial class AddDomainWindow : ReactiveWindow<AddDomainViewModel> {
    public AddDomainWindow() {
        InitializeComponent();

        // When Save or Cancel is clicked, close the window and return the result
        this.WhenActivated(action => {
            if (ViewModel != null) {
                action(ViewModel.SaveCommand.Subscribe(Close));
                action(ViewModel.CancelCommand.Subscribe(Close));
                action(ViewModel.OpenHeadersInteraction.RegisterHandler(async interaction => {
                    await DoShowHeadersDialogAsync(interaction);
                }));
            }
        });
    }

    private async Task DoShowHeadersDialogAsync(IInteractionContext<Dictionary<string, string>, Dictionary<string, string>?> interaction) {
        var dialog = new ExpectedHeadersWindow {
            ViewModel = new ExpectedHeadersViewModel(interaction.Input)
        };
        var result = await dialog.ShowDialog<Dictionary<string, string>?>(this);
        interaction.SetOutput(result);
    }
}


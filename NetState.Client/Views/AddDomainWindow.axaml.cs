using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;
using NetState.Shared.Models;
using ReactiveUI;

namespace NetState.Client.Views;

/* :: :: AddDomainWindow :: START :: */

/// <summary>
/// Code-behind for the Add/Edit monitored entry dialog window.
/// </summary>
public partial class AddDomainWindow : ReactiveWindow<AddDomainViewModel> {
    /// <summary>
    /// Initializes a new instance of the <see cref="AddDomainWindow"/> class.
    /// </summary>
    public AddDomainWindow() {
        InitializeComponent();

        this.WhenActivated(action => {
            if (ViewModel != null) {
                action(ViewModel.SaveCommand.Subscribe(Close));
                action(ViewModel.CancelCommand.Subscribe(Close));
                action(ViewModel.AddSelectedCommand.Subscribe(Close));
                action(ViewModel.OpenHeadersInteraction.RegisterHandler(DoShowHeadersDialogAsync));
                action(ViewModel.OpenMiscDnsValuesInteraction.RegisterHandler(DoShowMiscDnsValuesDialogAsync));
            }
        });
    }

    private async Task DoShowHeadersDialogAsync(IInteractionContext<Dictionary<string, string>, Dictionary<string, string>?> interaction) {
        ExpectedHeadersWindow dialog = new() {
            ViewModel = new ExpectedHeadersViewModel(interaction.Input)
        };
        Dictionary<string, string>? result = await dialog.ShowDialog<Dictionary<string, string>?>(this);
        interaction.SetOutput(result);
    }

    private async Task DoShowMiscDnsValuesDialogAsync(IInteractionContext<Dictionary<string, string>, Dictionary<string, string>?> interaction) {
        MiscDnsValuesWindow dialog = new() {
            ViewModel = new MiscDnsValuesViewModel(interaction.Input)
        };
        Dictionary<string, string>? result = await dialog.ShowDialog<Dictionary<string, string>?>(this);
        interaction.SetOutput(result);
    }
}

/* :: :: AddDomainWindow :: END :: */

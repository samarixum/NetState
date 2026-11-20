using System;
using Avalonia.ReactiveUI;
using NetState.Client.ViewModels;
using ReactiveUI;

namespace NetState.Client.Views;

/* :: :: MiscDnsValuesWindow :: START :: */

/// <summary>
/// Code-behind for the miscellaneous DNS values editor dialog window.
/// </summary>
public partial class MiscDnsValuesWindow : ReactiveWindow<MiscDnsValuesViewModel> {
    /// <summary>
    /// Initializes a new instance of the <see cref="MiscDnsValuesWindow"/> class.
    /// </summary>
    public MiscDnsValuesWindow() {
        InitializeComponent();

        this.WhenActivated(d => {
            if (ViewModel != null) {
                d(ViewModel.SaveCommand.Subscribe(Close));
                d(ViewModel.CancelCommand.Subscribe(Close));
            }
        });
    }
}

/* :: :: MiscDnsValuesWindow :: END :: */

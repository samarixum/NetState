using Avalonia.Controls;
using NetState.Client.ViewModels;

namespace NetState.Client;

public partial class MainWindow : Window {
    public MainWindow() {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}

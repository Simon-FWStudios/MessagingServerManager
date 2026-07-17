using System.ComponentModel;
using System.Windows;

namespace MessagingServerManager.App;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;
        IsEnabled = false;
        await viewModel.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }
}

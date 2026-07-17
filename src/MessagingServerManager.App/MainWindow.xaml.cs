using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MessagingServerManager.App;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int RoundCorners = 2;
    private bool _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyRoundedCorners();
    }
    private void ApplyRoundedCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var preference = RoundCorners;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;
        IsEnabled = false;
        try { await viewModel.ShutdownAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.GetBaseException().Message, "Shutdown warning", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _shutdownComplete = true; Close(); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

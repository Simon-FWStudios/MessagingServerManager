using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MessagingServerManager.Core;
using MessagingServerManager.Infrastructure;
using MessagingServerManager.ServerAdapters;
namespace MessagingServerManager.App;
public partial class App : Application
{
    private MainViewModel? _vm;
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        SplashWindow? splash = null;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            splash = new SplashWindow { StatusText = "Preparing application…" };
            splash.Show();
            var paths = new PathResolver();
            var store = new JsonConfigurationStore(paths);
            var tcp = new TcpHealthChecker();
            splash.StatusText = "Loading settings…";
            var settings = await store.LoadAsync("settings.json", new GlobalSettings());
            splash.StatusText = "Preparing server adapters…";
            IServerAdapter[] adapters = [new NatsServerAdapter(paths, tcp), new TibRvServerAdapter(paths, tcp)];
            _vm = new(paths, store, new ConfigurationTransferService(), adapters, settings);
            var window = new MainWindow { DataContext = _vm };
            MainWindow = window;
            splash.StatusText = "Loading server configuration…";
            await _vm.InitializeAsync();
            splash.StatusText = "Opening dashboard…";
            window.Show();
            splash.Close();
        }
        catch (ConfigurationLoadException ex)
        {
            splash?.Close();
            MessageBox.Show(ex.Message, "Configuration recovery required", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
        catch (Exception ex)
        {
            splash?.Close();
            MessageBox.Show(ex.GetBaseException().Message, "Application startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

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
    private async void Application_Startup(object sender, StartupEventArgs e){RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;var paths=new PathResolver();var store=new JsonConfigurationStore(paths);var tcp=new TcpHealthChecker();var settings=await store.LoadAsync("settings.json",new GlobalSettings());IServerAdapter[] adapters=[new NatsServerAdapter(paths,tcp),new TibRvServerAdapter(paths,tcp)];_vm=new(paths,store,new ConfigurationTransferService(),adapters,settings);var window=new MainWindow{DataContext=_vm};MainWindow=window;window.Show();await _vm.InitializeAsync();}
    private void Application_Exit(object sender,ExitEventArgs e)=>_vm?.Dispose();
}

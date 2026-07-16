using System.Diagnostics;using MessagingServerManager.Core;using MessagingServerManager.Infrastructure;
namespace MessagingServerManager.Infrastructure.Tests;
public class InfrastructureTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"MsmTests",Guid.NewGuid().ToString());
 [Fact]public void Path_resolution_expands_environment_and_relative_paths(){Environment.SetEnvironmentVariable("MSM_TEST","folder");var p=new PathResolver(root);Assert.Equal(Path.Combine(root,"folder","a.txt"),p.Resolve("%MSM_TEST%/a.txt"));}
 [Fact]public async Task Json_round_trips_and_keeps_backup(){var store=new JsonConfigurationStore(new(root));await store.SaveAsync("a.json",new GlobalSettings{MaximumLogLines=10});await store.SaveAsync("a.json",new GlobalSettings{MaximumLogLines=20});var loaded=await store.LoadAsync("a.json",new GlobalSettings());Assert.Equal(20,loaded.MaximumLogLines);Assert.True(File.Exists(Path.Combine(root,"a.json.bak")));}
 [Fact]public async Task Tcp_check_reports_unavailable(){var result=await new TcpHealthChecker().CheckAsync("127.0.0.1",1,TimeSpan.FromMilliseconds(100),default);Assert.False(result.IsHealthy);}
 [Fact]public void Process_identity_rejects_wrong_executable(){using var p=Process.GetCurrentProcess();var state=new RuntimeProcessState{ProcessId=p.Id,StartTimeUtc=p.StartTime.ToUniversalTime(),Executable="definitely-wrong.exe"};Assert.False(new ProcessIdentity().Matches(p,state));}
 public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}

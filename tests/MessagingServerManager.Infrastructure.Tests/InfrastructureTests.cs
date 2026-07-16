using System.Diagnostics;using MessagingServerManager.Core;using MessagingServerManager.Infrastructure;
namespace MessagingServerManager.Infrastructure.Tests;
public class InfrastructureTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"MsmTests",Guid.NewGuid().ToString());
 [Fact]public void Path_resolution_expands_environment_and_relative_paths(){Environment.SetEnvironmentVariable("MSM_TEST","folder");var p=new PathResolver(root);Assert.Equal(Path.Combine(root,"folder","a.txt"),p.Resolve("%MSM_TEST%/a.txt"));}
 [Fact]public async Task Json_round_trips_and_keeps_backup(){var store=new JsonConfigurationStore(new(root));await store.SaveAsync("a.json",new GlobalSettings{MaximumLogLines=10});await store.SaveAsync("a.json",new GlobalSettings{MaximumLogLines=20});var loaded=await store.LoadAsync("a.json",new GlobalSettings());Assert.Equal(20,loaded.MaximumLogLines);Assert.True(File.Exists(Path.Combine(root,"a.json.bak")));}
 [Fact]public async Task Tcp_check_reports_unavailable(){var result=await new TcpHealthChecker().CheckAsync("127.0.0.1",1,TimeSpan.FromMilliseconds(100),default);Assert.False(result.IsHealthy);}
 [Fact]public void Process_identity_rejects_wrong_executable(){using var p=Process.GetCurrentProcess();var state=new RuntimeProcessState{ProcessId=p.Id,StartTimeUtc=p.StartTime.ToUniversalTime(),Executable="definitely-wrong.exe"};Assert.False(new ProcessIdentity().Matches(p,state));}
 [Fact]public async Task Log_tail_reads_last_lines_then_appends_without_duplicates(){Directory.CreateDirectory(root);var path=Path.Combine(root,"large.log");await File.WriteAllLinesAsync(path,Enumerable.Range(1,5000).Select(x=>$"line-{x}"));var reader=new LogTailReader();var initial=await reader.ReadTailAsync(path,3);Assert.Equal(["line-4998","line-4999","line-5000"],initial);await File.AppendAllTextAsync(path,"line-5001"+Environment.NewLine);var appended=await reader.ReadTailAsync(path,3);Assert.Equal(["line-4999","line-5000","line-5001"],appended);}
 [Fact]public async Task Log_tail_resets_after_truncation(){Directory.CreateDirectory(root);var path=Path.Combine(root,"rotate.log");await File.WriteAllTextAsync(path,"old-1\nold-2\n");var reader=new LogTailReader();_ = await reader.ReadTailAsync(path,10);await File.WriteAllTextAsync(path,"new-1\n");var lines=await reader.ReadTailAsync(path,10);Assert.Equal(["new-1"],lines);}
 [Fact]public void Runtime_recovery_validates_and_attaches_existing_process(){using var current=Process.GetCurrentProcess();var executable=current.MainModule!.FileName!;var definition=new ServerDefinition{Executable=executable};var state=new RuntimeProcessState{ServerId=definition.Id,ProcessId=current.Id,StartTimeUtc=current.StartTime.ToUniversalTime(),Executable=executable};using var manager=new ProcessManager([new ExistingProcessAdapter()],new GlobalSettings());Assert.True(manager.TryRecover(definition,state));Assert.Equal(current.Id,manager.GetRuntimeStates().Single().ProcessId);}
 [Fact]public async Task Immediate_exit_never_leaves_dead_registration(){var definition=new ServerDefinition{Executable="powershell.exe"};using var manager=new ProcessManager([new TestProcessAdapter(immediate:true)],new GlobalSettings());try{await manager.StartAsync(definition);}catch(InvalidOperationException){}await Task.Delay(250);Assert.Null(manager.Get(definition.Id));}
 [Fact]public async Task Restart_waits_for_old_process_removal_before_starting_replacement(){var definition=new ServerDefinition{Executable="powershell.exe",GracefulStopTimeoutSeconds=2};using var manager=new ProcessManager([new TestProcessAdapter(immediate:false)],new GlobalSettings());await manager.StartAsync(definition);var first=manager.Get(definition.Id)!.Process.Id;await manager.RestartAsync(definition);var second=manager.Get(definition.Id)!.Process.Id;Assert.NotEqual(first,second);await manager.StopAsync(definition);}
 public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}

 sealed class ExistingProcessAdapter:IServerAdapter
 {
  public ServerType ServerType=>ServerType.Nats;
  public ProcessStartInfo BuildStartInfo(ServerDefinition definition,GlobalSettings settings)=>throw new NotSupportedException();
  public Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition,RunningProcessInfo? process,CancellationToken cancellationToken)=>Task.FromResult(ServerHealthResult.Healthy());
  public Task<StopResult> StopAsync(ServerDefinition definition,RunningProcessInfo process,CancellationToken cancellationToken)=>Task.FromResult(new StopResult(true,false));
  public bool MatchesProcess(ServerDefinition definition,Process process)=>ProcessIdentity.ExecutableMatches(process,definition.Executable);
 }
 sealed class TestProcessAdapter(bool immediate):IServerAdapter
 {
  public ServerType ServerType=>ServerType.Nats;
  public ProcessStartInfo BuildStartInfo(ServerDefinition definition,GlobalSettings settings)=>new(){FileName="powershell.exe",Arguments=immediate?"-NoProfile -Command exit 7":"-NoProfile -Command Start-Sleep -Seconds 30",UseShellExecute=false,CreateNoWindow=true};
  public Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition,RunningProcessInfo? process,CancellationToken cancellationToken)=>Task.FromResult(ServerHealthResult.Healthy());
  public async Task<StopResult> StopAsync(ServerDefinition definition,RunningProcessInfo process,CancellationToken cancellationToken){try{using var p=Process.GetProcessById(process.ProcessId);p.Kill(true);await p.WaitForExitAsync(cancellationToken);return new(true,true);}catch(ArgumentException){return new(true,false);}}
  public bool MatchesProcess(ServerDefinition definition,Process process)=>true;
 }
}

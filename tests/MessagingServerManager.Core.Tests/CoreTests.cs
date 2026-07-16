using MessagingServerManager.Core;using MessagingServerManager.Infrastructure;using MessagingServerManager.ServerAdapters;
namespace MessagingServerManager.Core.Tests;
public class CoreTests
{
 [Fact]public void Validation_detects_duplicate_names_and_ports(){var a=new ServerDefinition{Name="A",Nats=new(){ClientPort=4222,MonitoringPort=8222}};var b=new ServerDefinition{Name="A",Nats=new(){ClientPort=4222,MonitoringPort=8223}};var result=ServerValidator.Validate(b,[a,b],Path.GetFullPath);Assert.Contains(result.Errors,x=>x.Contains("unique"));Assert.Contains(result.Errors,x=>x.Contains("4222"));}
 [Fact]public void Cpu_usage_uses_cpu_wall_and_core_deltas()=>Assert.Equal(25,CpuUsageCalculator.Calculate(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),2));
 [Fact]public void Nats_config_mode_quotes_resolved_path(){var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var adapter=new NatsServerAdapter(new(root),new());var d=new ServerDefinition{LaunchMode=LaunchMode.ConfigFile,ConfigFilePath="a file.conf"};Assert.Contains($"-c \"{Path.Combine(root,"a file.conf")}\"",adapter.BuildArguments(d));}
 [Fact]public void Tibco_managed_options_generates_ports(){var adapter=new TibRvServerAdapter(new(Path.GetTempPath()),new());var d=new ServerDefinition{ServerType=ServerType.TibcoRendezvous,TibRv=new(){Service=7500,HttpAdministrationPort=7580}};Assert.Contains("-service 7500",adapter.BuildArguments(d));Assert.Contains("-http 7580",adapter.BuildArguments(d));}
}

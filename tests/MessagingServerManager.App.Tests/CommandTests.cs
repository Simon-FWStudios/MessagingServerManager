using MessagingServerManager.App;
using MessagingServerManager.Core;
using MessagingServerManager.Infrastructure;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MessagingServerManager.App.Tests;

public sealed class CommandTests
{
    [Fact]
    public async Task Async_command_routes_exceptions_to_the_error_handler()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = AsyncCommandErrors.Handler;
        try
        {
            AsyncCommandErrors.Handler = ex => reported.TrySetResult(ex);
            var command = new AsyncRelayCommand(async () => { await Task.Yield(); throw new InvalidOperationException("boom"); });
            command.Execute(null);
            var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("boom", exception.Message);
        }
        finally { AsyncCommandErrors.Handler = previous; }
    }

    [Fact]
    public async Task Synchronous_command_routes_exceptions_to_the_error_handler()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = AsyncCommandErrors.Handler;
        try
        {
            AsyncCommandErrors.Handler = ex => reported.TrySetResult(ex);
            new RelayCommand(() => throw new InvalidOperationException("sync boom")).Execute(null);
            Assert.Equal("sync boom", (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        }
        finally { AsyncCommandErrors.Handler = previous; }
    }

    [Fact]
    public void Telemetry_failure_marks_retained_data_as_stale_and_resets_rates()
    {
        var row = new ServerRowViewModel(new ServerDefinition());
        var first = new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(10), 1, 100, 2, 3, 10, 5, 100, 50, 0, 100, 2, 0, 0, 0, "", "", "", DateTimeOffset.UtcNow, true, "{}");
        row.ApplyTelemetry(first);
        row.MarkTelemetryAvailable();
        row.MarkTelemetryUnavailable();
        Assert.True(row.IsTelemetryStale);
        Assert.NotNull(row.LastTelemetrySuccess);
        Assert.Contains("Stale", row.TelemetryFreshnessText);
        Assert.Equal("—", row.MessageRateText);
        Assert.True(row.HasRawTelemetry);
    }

    [Fact]
    public void Message_rate_columns_keep_thousands_suffixes_on_both_directions()
    {
        var row = new ServerRowViewModel(new ServerDefinition());
        var sample = DateTimeOffset.UtcNow;
        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(10), 1, 100, 2, 3, 0, 0, 100, 50, 0, 100, 2, 0, 0, 0, "", "", "", sample, true, "{}"));
        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(11), 1, 100, 2, 3, 10000, 10000, 100, 50, 0, 100, 2, 0, 0, 0, "", "", "", sample.AddSeconds(1), true, "{}"));

        Assert.Equal("10k", row.InMessageRateText);
        Assert.Equal("10k", row.OutMessageRateText);
        Assert.Equal("↓ 10k  ↑ 10k", row.MessageRateText);
        Assert.Equal("In 10k/s • Out 10k/s", row.MessageRateLineText);
    }

    [Fact]
    public void Remote_nats_telemetry_updates_cpu_and_memory_cards()
    {
        var row = new ServerRowViewModel(new ServerDefinition());

        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(10), 12.3, 128 * 1048576, 2, 3, 0, 0, 100, 50, 0, 100, 2, 0, 0, 0, "", "", "", DateTimeOffset.UtcNow, true, "{}"));

        Assert.Equal("12.3%", row.CpuText);
        Assert.Equal("128.0 MB", row.MemoryText);
    }

    [Fact]
    public void Metric_sparklines_keep_only_the_configured_history_window()
    {
        var row = new ServerRowViewModel(new ServerDefinition());
        var sample = DateTimeOffset.UtcNow;

        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(10), 1, 100, 10, 3, 0, 0, 100, 50, 0, 100, 2, 0, 0, 0, "", "", "", sample.AddMinutes(-10), true, "{}"));
        row.RecordMetricSample(sample.AddMinutes(-10), 5);
        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(11), 2, 200, 20, 3, 10, 10, 200, 100, 0, 100, 2, 0, 0, 0, "", "", "", sample, true, "{}"));
        row.RecordMetricSample(sample, 5);
        row.ApplyTelemetry(new RemoteServerTelemetry("id", "name", "1", 4222, 8222, TimeSpan.FromSeconds(12), 3, 300, 25, 3, 20, 20, 300, 150, 0, 100, 2, 0, 0, 0, "", "", "", sample.AddSeconds(1), true, "{}"));
        row.RecordMetricSample(sample.AddSeconds(1), 5);

        Assert.Contains("2 sample", row.SparklineTooltip);
        Assert.Equal(2, row.ConnectionsSparkline.Count);
        Assert.Equal("20 → 25", row.ConnectionsSparklineScale);
        Assert.Equal("0/s → 20/s", row.MessageRateSparklineScale);
        Assert.Contains("Peak: 20/s", row.MessageRateSparklineTooltip);

        row.ClearMetricHistory();
        Assert.Empty(row.ConnectionsSparkline);
        Assert.Equal("", row.ConnectionsSparklineScale);
    }

    [Fact]
    public void Enabling_and_disabling_reconciles_row_status_immediately()
    {
        var definition = new ServerDefinition { Enabled = false };
        var row = new ServerRowViewModel(definition);
        Assert.Equal(ServerStatus.Disabled, row.Status);
        definition.Enabled = true;
        row.ReconcileDefinitionState();
        Assert.Equal(ServerStatus.Stopped, row.Status);
        Assert.True(row.CanStart);
        definition.Enabled = false;
        row.ReconcileDefinitionState();
        Assert.Equal(ServerStatus.Disabled, row.Status);
        Assert.False(row.CanStart);
    }

    [Fact]
    public void Stopped_server_uses_a_neutral_status_indicator()
    {
        var row = new ServerRowViewModel(new ServerDefinition());
        Assert.Equal(ServerStatus.Stopped, row.Status);
        Assert.Same(Brushes.SlateGray, row.StatusBrush);
    }

    [Fact]
    public void Blank_working_directory_is_displayed_as_automatic()
    {
        var row = new ServerRowViewModel(new ServerDefinition { WorkingDirectory = "" });
        Assert.Equal("Automatic", row.WorkingDirectoryDisplay);
    }

    [Fact]
    public void Server_details_toggle_updates_expanded_and_collapsed_visibility()
    {
        using var viewModel = new MainViewModel(new(Path.GetTempPath()), new MemoryStore(), new(), [new RefreshAdapter()], new());

        Assert.True(viewModel.IsServerDetailsExpanded);
        Assert.Equal(Visibility.Visible, viewModel.ServerDetailsExpandedVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ServerDetailsCollapsedVisibility);

        viewModel.ToggleServerDetailsCommand.Execute(null);

        Assert.False(viewModel.IsServerDetailsExpanded);
        Assert.Equal(Visibility.Collapsed, viewModel.ServerDetailsExpandedVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ServerDetailsCollapsedVisibility);
    }

    [Fact]
    public void Last_exit_code_hides_empty_and_negative_values()
    {
        var row = new ServerRowViewModel(new ServerDefinition());
        Assert.Equal("—", row.LastExitCodeText);
        row.LastExitCode = -1;
        Assert.Equal("—", row.LastExitCodeText);
        row.LastExitCode = 2;
        Assert.Equal("2", row.LastExitCodeText);
    }

    [Fact]
    public void Server_summary_notifies_when_rows_or_status_change()
    {
        using var viewModel = new MainViewModel(new(Path.GetTempPath()), new MemoryStore(), new(), [new RefreshAdapter()], new());
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var row = new ServerRowViewModel(new() { Name = "Observed" });

        viewModel.Servers.Add(row);
        row.Status = ServerStatus.Running;

        Assert.Contains(nameof(MainViewModel.ServerSummaryText), notifications);
        Assert.StartsWith("1 running", viewModel.ServerSummaryText);
        Assert.EndsWith("1 total", viewModel.ServerSummaryText);
    }

    [Fact]
    public void Server_summary_includes_failed_and_invalid_states()
    {
        using var viewModel = new MainViewModel(new(Path.GetTempPath()), new MemoryStore(), new(), [new RefreshAdapter()], new());
        viewModel.Servers.Add(new(new() { Name = "Stopped" }) { Status = ServerStatus.Stopped });
        viewModel.Servers.Add(new(new() { Name = "Failed" }) { Status = ServerStatus.Failed });
        viewModel.Servers.Add(new(new() { Name = "Invalid" }) { Status = ServerStatus.Invalid });
        viewModel.Servers.Add(new(new() { Name = "Disabled", Enabled = false }));

        Assert.Equal("0 running  •  1 stopped  •  1 failed  •  1 invalid  •  1 disabled  •  4 total", viewModel.ServerSummaryText);
    }

    [Fact]
    public void About_window_loads_from_embedded_markdown()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                app = Application.Current ?? new Application();
                var window = new AboutWindow();
                Assert.Contains("Messaging Server Manager", window.AboutText);
                Assert.Contains("1.0.0", window.AboutText);
                window.Show();
                window.UpdateLayout();
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public async Task Refresh_failure_isolated_to_one_row()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var viewModel = new MainViewModel(new(root), new MemoryStore(), new(), [new RefreshAdapter()], new());
            var failing = new ServerRowViewModel(new() { Name = "Fail", Location = ServerLocation.Remote });
            var healthy = new ServerRowViewModel(new() { Name = "Healthy", Location = ServerLocation.Remote });
            viewModel.Servers.Add(failing);
            viewModel.Servers.Add(healthy);

            await viewModel.RefreshAllAsync();

            Assert.Equal(ServerStatus.Failed, failing.Status);
            Assert.Equal(ServerStatus.Running, healthy.Status);
            Assert.NotEqual(default, viewModel.LastUpdated);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_import_persistence_restores_previous_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryStore { FailNextSettingsSave = true };
            var settings = new GlobalSettings { MaximumLogLines = 100 };
            using var viewModel = new MainViewModel(new(root), store, new(), [new RefreshAdapter()], settings);
            viewModel.Servers.Add(new(new() { Name = "Original" }));
            var imported = new PortableConfigurationBundle
            {
                Settings = new() { MaximumLogLines = 999 },
                Servers = [new() { Name = "Imported" }]
            };

            await Assert.ThrowsAsync<IOException>(() => viewModel.PersistImportedConfigurationAsync(imported));

            var restored = Assert.IsType<ConfigurationEnvelope<List<ServerDefinition>>>(store.Values["servers.json"]);
            Assert.Equal("Original", Assert.Single(restored.Data).Name);
            Assert.Equal(100, Assert.IsType<GlobalSettings>(store.Values["settings.json"]).MaximumLogLines);
            Assert.Equal("Original", Assert.Single(viewModel.Servers).Name);
            Assert.Equal(100, viewModel.Settings.MaximumLogLines);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_import_preparation_restores_generated_configuration_files()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var resolver = new PathResolver(root);
            var existing = resolver.Resolve("generated/existing.conf");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllTextAsync(existing, "original");
            var store = new MemoryStore();
            using var viewModel = new MainViewModel(resolver, store, new(), [new PreparingAdapter(resolver)], new());
            var bundle = new PortableConfigurationBundle
            {
                Servers =
                [
                    new() { Name = "Prepared", LaunchMode = LaunchMode.ConfigFile, ManageConfigFile = true, ConfigFilePath = "generated/existing.conf" },
                    new() { Name = "Fail", LaunchMode = LaunchMode.ConfigFile, ManageConfigFile = true, ConfigFilePath = "generated/new.conf" }
                ]
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.PersistImportedConfigurationAsync(bundle));

            Assert.Equal("original", await File.ReadAllTextAsync(existing));
            Assert.False(File.Exists(resolver.Resolve("generated/new.conf")));
            Assert.Empty(store.Values);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Shutdown_completes_when_runtime_persistence_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var previous = AsyncCommandErrors.Handler;
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            AsyncCommandErrors.Handler = ex => reported.TrySetResult(ex);
            var store = new MemoryStore { FailRuntimeSaves = true };
            var viewModel = new MainViewModel(new(root), store, new(), [new RefreshAdapter()], new());

            await viewModel.ShutdownAsync();

            Assert.Contains("closed safely", (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        }
        finally { AsyncCommandErrors.Handler = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        public Dictionary<string, object> Values { get; } = [];
        public bool FailNextSettingsSave { get; set; }
        public bool FailRuntimeSaves { get; set; }
        public Task<T> LoadAsync<T>(string fileName, T fallback, CancellationToken cancellationToken = default) => Task.FromResult(fallback);
        public Task SaveAsync<T>(string fileName, T value, CancellationToken cancellationToken = default)
        {
            if (fileName == "settings.json" && FailNextSettingsSave) { FailNextSettingsSave = false; throw new IOException("simulated save failure"); }
            if (fileName == "runtime.json" && FailRuntimeSaves) throw new IOException("simulated runtime failure");
            Values[fileName] = value!;
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshAdapter : IServerAdapter
    {
        public ServerType ServerType => ServerType.Nats;
        public ProcessStartInfo BuildStartInfo(ServerDefinition definition, GlobalSettings settings) => new();
        public Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition, RunningProcessInfo? process, CancellationToken cancellationToken)
            => definition.Name == "Fail" ? throw new InvalidOperationException("simulated refresh failure") : Task.FromResult(ServerHealthResult.Healthy());
        public Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo process, CancellationToken cancellationToken) => Task.FromResult(new StopResult(true, false));
        public bool MatchesProcess(ServerDefinition definition, Process process) => false;
    }

    private sealed class PreparingAdapter(PathResolver paths) : IServerAdapter
    {
        public ServerType ServerType => ServerType.Nats;
        public ProcessStartInfo BuildStartInfo(ServerDefinition definition, GlobalSettings settings) => new();
        public async Task PrepareAsync(ServerDefinition definition, GlobalSettings settings, CancellationToken cancellationToken)
        {
            var path = paths.Resolve(definition.ConfigFilePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "prepared", cancellationToken);
            if (definition.Name == "Fail") throw new InvalidOperationException("simulated preparation failure");
        }
        public Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition, RunningProcessInfo? process, CancellationToken cancellationToken) => Task.FromResult(ServerHealthResult.Healthy());
        public Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo process, CancellationToken cancellationToken) => Task.FromResult(new StopResult(true, false));
        public bool MatchesProcess(ServerDefinition definition, Process process) => false;
    }
}

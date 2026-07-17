using MessagingServerManager.App;
using MessagingServerManager.Core;

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
}

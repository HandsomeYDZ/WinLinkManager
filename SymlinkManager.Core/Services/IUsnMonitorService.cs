namespace SymlinkManager.Core.Services;

public interface IUsnMonitorService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    bool IsFallbackMode { get; }
}

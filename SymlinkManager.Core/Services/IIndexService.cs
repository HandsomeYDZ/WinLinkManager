using SymlinkManager.Core.Models;

namespace SymlinkManager.Core.Services;

public interface IIndexService
{
    Task InitializeAsync();
    Task<bool> HasExistingIndexAsync();
    Task UpsertAsync(SymlinkEntry entry);
    Task<List<SymlinkEntry>> GetAllAsync();
    Task<List<ScanDirectoryConfig>> GetScanDirectoriesAsync();
    Task SaveScanDirectoriesAsync(List<ScanDirectoryConfig> dirs);
    Task RebuildIndexAsync();
    CancellationTokenSource StartBatchPersist(CancellationToken ct);
}

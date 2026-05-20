using SymlinkManager.Core.Models;

namespace SymlinkManager.Core.Services;

public interface IScannerService
{
    IAsyncEnumerable<SymlinkEntry> FullScanAsync(
        List<ScanDirectoryConfig> scanDirs,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);
}

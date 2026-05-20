namespace SymlinkManager.Core.Models;

public class ScanProgress
{
    public long TotalScanned { get; set; }
    public long LinksFound { get; set; }
    public string? CurrentDirectory { get; set; }
    public bool IsComplete { get; set; }
}

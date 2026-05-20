namespace SymlinkManager.Core.Models;

public class ScanDirectoryConfig
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool IsExcluded { get; set; }
    public string AddedTime { get; set; } = string.Empty;
}

namespace SymlinkManager.Core.Models;

public class WhitelistEntry
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string AddedTime { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
}

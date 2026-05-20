namespace SymlinkManager.Core.Models;

public class SymlinkEntry
{
    public string LinkPath { get; set; } = string.Empty;
    public string LinkName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public SymlinkType LinkType { get; set; }
    public string CreationTime { get; set; } = string.Empty;
    public LinkStatus Status { get; set; } = LinkStatus.Valid;
    public bool InWhitelist { get; set; }
    public string LastSeenTime { get; set; } = string.Empty;

    public string LinkTypeDisplay => LinkType switch
    {
        SymlinkType.FileSymlink => "文件符号链接",
        SymlinkType.DirectorySymlink => "目录符号链接(/D)",
        SymlinkType.Junction => "交接点(/J)",
        _ => "未知"
    };

    public string StatusDisplay => Status == LinkStatus.Valid ? "有效" : "已失效";
}

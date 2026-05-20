using SymlinkManager.App.ViewModels.Base;
using SymlinkManager.Core.Models;

namespace SymlinkManager.App.ViewModels;

public class ConversionPreviewViewModel : ViewModelBase
{
    public SymlinkEntry? Entry { get; set; }
    public SymlinkType NewType { get; set; }

    public string CurrentTypeDisplay => Entry?.LinkTypeDisplay ?? "";
    public string NewTypeDisplay => NewType switch
    {
        SymlinkType.Junction => "交接点(/J)",
        SymlinkType.DirectorySymlink => "目录符号链接(/D)",
        _ => "未知"
    };

    public string Description => Entry == null ? "" :
        $"将「{Entry.LinkName}」从 {CurrentTypeDisplay} 转换为 {NewTypeDisplay}";

    public ConversionPreviewViewModel() { }
}

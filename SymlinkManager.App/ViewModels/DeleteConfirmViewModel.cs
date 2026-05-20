using SymlinkManager.App.ViewModels.Base;
using SymlinkManager.Core.Models;

namespace SymlinkManager.App.ViewModels;

public class DeleteConfirmViewModel : ViewModelBase
{
    public SymlinkEntry? Entry { get; set; }

    public string Message => Entry == null ? "" :
        $"确定要删除符号链接「{Entry.LinkName}」吗？\n\n路径: {Entry.LinkPath}\n目标: {Entry.TargetPath}\n\n此操作不可恢复。";

    public DeleteConfirmViewModel() { }
}

using System.IO;
using SymlinkManager.App.ViewModels.Base;
using SymlinkManager.Core.Models;
using SymlinkManager.Core.Services;

namespace SymlinkManager.App.ViewModels;

public class CreateLinkViewModel : ViewModelBase
{
    private readonly ISymlinkService _symlinkService;
    private readonly IIndexService _indexService;

    public CreateLinkViewModel(ISymlinkService symlinkService, IIndexService indexService)
    {
        _symlinkService = symlinkService;
        _indexService = indexService;
    }

    private string _linkPath = string.Empty;
    public string LinkPath
    {
        get => _linkPath;
        set => SetProperty(ref _linkPath, value);
    }

    private string _targetPath = string.Empty;
    public string TargetPath
    {
        get => _targetPath;
        set => SetProperty(ref _targetPath, value);
    }

    private SymlinkType _linkType;
    public SymlinkType LinkType
    {
        get => _linkType;
        set => SetProperty(ref _linkType, value);
    }

    public SymlinkType[] LinkTypes { get; } = { SymlinkType.FileSymlink, SymlinkType.DirectorySymlink, SymlinkType.Junction };

    public SymlinkEntry? CreatedEntry { get; private set; }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool Confirm()
    {
        if (string.IsNullOrWhiteSpace(LinkPath))
        {
            ErrorMessage = "请输入链接路径";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            ErrorMessage = "请输入目标路径";
            return false;
        }

        if (_symlinkService.Exists(LinkPath))
        {
            ErrorMessage = "目标路径已存在";
            return false;
        }

        if (!_symlinkService.CreateSymlink(LinkPath, TargetPath, LinkType))
        {
            ErrorMessage = "创建符号链接失败，请确认是否有管理员权限";
            return false;
        }

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        CreatedEntry = new SymlinkEntry
        {
            LinkPath = LinkPath,
            LinkName = Path.GetFileName(LinkPath),
            TargetPath = TargetPath,
            LinkType = LinkType,
            CreationTime = now,
            Status = LinkStatus.Valid,
            LastSeenTime = now
        };

        _ = _indexService.UpsertAsync(CreatedEntry);
        return true;
    }
}

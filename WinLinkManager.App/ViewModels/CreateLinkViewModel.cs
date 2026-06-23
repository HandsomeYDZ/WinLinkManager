using System.IO;
using WinLinkManager.App.ViewModels.Base;
using WinLinkManager.Core.Models;
using WinLinkManager.Core.Services;

namespace WinLinkManager.App.ViewModels;

/// <summary>
/// 创建/编辑符号链接对话框的 ViewModel，处理路径验证和链接创建。
/// </summary>
public class CreateLinkViewModel : ViewModelBase
{
    private readonly ILinkService _linkService;
    private readonly IIndexService _indexService;
    private readonly LinkEntry? _originalEntry;
    private bool _skipTargetCheck;

    /// <param name="originalEntry">不为 null 时表示编辑模式，预填原始链接信息。</param>
    public CreateLinkViewModel(ILinkService linkService, IIndexService indexService, LinkEntry? originalEntry = null)
    {
        _linkService = linkService;
        _indexService = indexService;
        _originalEntry = originalEntry;

        // 编辑模式下预填原有路径和类型
        if (_originalEntry is not null)
        {
            DialogTitle = "编辑符号链接";
            ConfirmButtonLabel = "保存";
            LinkPath = _originalEntry.LinkPath;
            TargetPath = _originalEntry.TargetPath;
            LinkType = _originalEntry.LinkType;
        }
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

    // 新建目录链接时默认使用真正的 NTFS 交接点，而不是文件符号链接。
    // 编辑模式会在构造函数中覆盖为原条目的实际类型。
    private LinkType _linkType = LinkType.Junction;
    public LinkType LinkType
    {
        get => _linkType;
        set => SetProperty(ref _linkType, value);
    }

    /// <summary> 可供选择的链接类型列表。 </summary>
    public LinkType[] LinkTypes { get; } = { LinkType.FileLink, LinkType.DirectoryLink, LinkType.Junction };

    /// <summary> 确认后生成的链接条目。 </summary>
    public LinkEntry? CreatedEntry { get; private set; }

    public string DialogTitle { get; } = "新建符号链接";

    /// <summary>确认按钮的文字（新建时显示"创建"，编辑时显示"保存"）</summary>
    public string ConfirmButtonLabel { get; } = "创建";

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary> 目标路径不存在时设为 true，由 Dialog 询问用户如何处理。 </summary>
    public bool TargetNotFound { get; private set; }

    /// <summary> 验证输入并执行创建或编辑操作，返回是否成功。 </summary>
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

        // 目标路径不存在且未跳过检查 → 设标志让 Dialog 询问用户
        if (!_skipTargetCheck && !Directory.Exists(TargetPath) && !File.Exists(TargetPath))
        {
            TargetNotFound = true;
            return false;
        }

        TargetNotFound = false;
        ErrorMessage = null;

        if (_originalEntry is null)
            return ConfirmCreate();

        return ConfirmEdit();
    }

    /// <summary> 用户选择"是"：创建目标目录后重试 Confirm。 </summary>
    public void CreateTargetAndRetry()
    {
        try { Directory.CreateDirectory(TargetPath); }
        catch (Exception ex)
        {
            ErrorMessage = $"创建目标目录失败: {ex.Message}";
        }
    }

    /// <summary> 用户选择"否"：跳过目标检查，允许指向不存在的路径。 </summary>
    public void SkipTargetCheckAndRetry()
    {
        _skipTargetCheck = true;
    }

    /// <summary> 创建新链接：检查冲突后调用服务创建。 </summary>
    private bool ConfirmCreate()
    {
        if (_linkService.Exists(LinkPath))
        {
            ErrorMessage = "链接路径已存在";
            return false;
        }

        if (!_linkService.CreateLink(LinkPath, TargetPath, LinkType))
        {
            ErrorMessage = "创建符号链接失败，请确认是否有管理员权限";
            return false;
        }

        if (!EnsureCreatedType())
        {
            _linkService.DeleteLink(LinkPath, _linkService.DetectType(LinkPath));
            return false;
        }

        CreateEntry();
        _ = _indexService.UpsertAsync(CreatedEntry!);
        return true;
    }

    /// <summary> 编辑已有链接：路径相同时原地替换，不同则新建后删除旧链接。 </summary>
    private bool ConfirmEdit()
    {
        var originalPath = _originalEntry!.LinkPath;
        var originalType = _originalEntry.LinkType;

        // 路径未变：删除旧链接后重建（相当于覆盖）
        if (originalPath == LinkPath)
        {
            _linkService.DeleteLink(originalPath, originalType);

            if (!_linkService.CreateLink(LinkPath, TargetPath, LinkType))
            {
                // 创建失败时尝试恢复原链接
                _linkService.CreateLink(originalPath, _originalEntry.TargetPath, originalType);
                ErrorMessage = "更新链接失败，请确认是否有管理员权限";
                return false;
            }

            if (!EnsureCreatedType())
            {
                _linkService.DeleteLink(LinkPath, _linkService.DetectType(LinkPath));
                _linkService.CreateLink(originalPath, _originalEntry.TargetPath, originalType);
                return false;
            }
        }
        else
        {
            // 路径改变：检查新路径冲突，创建新链接后再删旧链接
            if (_linkService.Exists(LinkPath))
            {
                ErrorMessage = "新的链接路径已存在";
                return false;
            }

            if (!_linkService.CreateLink(LinkPath, TargetPath, LinkType))
            {
                ErrorMessage = "创建新链接失败，请确认是否有管理员权限";
                return false;
            }

            if (!EnsureCreatedType())
            {
                _linkService.DeleteLink(LinkPath, _linkService.DetectType(LinkPath));
                return false;
            }

            try
            {
                _linkService.DeleteLink(originalPath, originalType);
            }
            catch
            {
                ErrorMessage = "旧链接已创建新链接，但删除旧链接失败";
                return false;
            }
        }

        CreateEntry();
        return true;
    }

    /// <summary>创建后从磁盘回读类型，防止界面/数据库类型与重解析点不一致。</summary>
    private bool EnsureCreatedType()
    {
        var actualType = _linkService.DetectType(LinkPath);
        if (actualType == LinkType)
            return true;

        ErrorMessage = $"链接类型创建不一致：请求 {LinkType}，实际 {actualType}";
        return false;
    }

    /// <summary> 根据当前输入构造 LinkEntry 对象，编辑模式下保留原有白名单状态。 </summary>
    private void CreateEntry()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var indexedTargetPath = GetIndexedTargetPath();
        CreatedEntry = new LinkEntry
        {
            LinkPath = LinkPath,
            LinkName = Path.GetFileName(LinkPath),
            TargetPath = indexedTargetPath,
            LinkType = LinkType,
            CreationTime = _originalEntry?.CreationTime ?? now,
            Status = Directory.Exists(indexedTargetPath) || File.Exists(indexedTargetPath)
                ? LinkStatus.Valid
                : LinkStatus.Broken,
            InWhitelist = _originalEntry?.InWhitelist ?? false,
            LastSeenTime = now
        };
    }

    /// <summary>索引使用完整目标路径，避免相对路径依赖应用当前工作目录。</summary>
    private string GetIndexedTargetPath()
    {
        if (Path.IsPathRooted(TargetPath))
            return Path.GetFullPath(TargetPath);

        var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(LinkPath));
        return string.IsNullOrEmpty(linkDirectory)
            ? TargetPath
            : Path.GetFullPath(Path.Combine(linkDirectory, TargetPath));
    }
}

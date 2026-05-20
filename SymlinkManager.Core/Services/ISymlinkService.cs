using SymlinkManager.Core.Models;

namespace SymlinkManager.Core.Services;

public interface ISymlinkService
{
    bool CreateSymlink(string linkPath, string targetPath, SymlinkType type);
    void DeleteSymlink(string linkPath, SymlinkType type);
    ConvertResult ConvertType(string linkPath, SymlinkType currentType, SymlinkType newType, string newTarget);
    SymlinkType DetectType(string linkPath);
    bool Exists(string linkPath);
}

public class ConvertResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

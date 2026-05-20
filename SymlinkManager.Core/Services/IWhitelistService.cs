namespace SymlinkManager.Core.Services;

public interface IWhitelistService
{
    Task AddAutoAsync(string path);
    Task AddManualAsync(string path);
    Task RemoveAsync(string path);
    Task<bool> IsInWhitelistAsync(string path);
    Task<List<string>> GetAllPathsAsync();
}

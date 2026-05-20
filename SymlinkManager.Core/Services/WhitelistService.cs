using SymlinkManager.Core.Data;

namespace SymlinkManager.Core.Services;

public class WhitelistService : IWhitelistService
{
    private readonly AppDbContext _dbContext;
    private readonly IIndexService _indexService;

    public WhitelistService(AppDbContext dbContext, IIndexService indexService)
    {
        _dbContext = dbContext;
        _indexService = indexService;
    }

    public async Task AddAutoAsync(string path)
        => await _dbContext.AddWhitelistAsync(path, "auto");

    public async Task AddManualAsync(string path)
        => await _dbContext.AddWhitelistAsync(path, "manual");

    public async Task RemoveAsync(string path)
        => await _dbContext.RemoveWhitelistAsync(path);

    public async Task<bool> IsInWhitelistAsync(string path)
    {
        var paths = await _dbContext.GetAllWhitelistPathsAsync();
        return paths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<string>> GetAllPathsAsync()
        => await _dbContext.GetAllWhitelistPathsAsync();
}

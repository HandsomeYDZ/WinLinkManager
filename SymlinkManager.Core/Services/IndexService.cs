using System.Collections.Concurrent;
using SymlinkManager.Core.Data;
using SymlinkManager.Core.Models;

namespace SymlinkManager.Core.Services;

public class IndexService : IIndexService
{
    private readonly AppDbContext _db;
    private readonly ConcurrentDictionary<string, SymlinkEntry> _cache = new();
    private ConcurrentDictionary<string, byte> _dirtyKeys = new();

    public IndexService(AppDbContext db)
    {
        _db = db;
    }

    public async Task InitializeAsync()
    {
        var links = await _db.LoadAllLinksAsync();
        foreach (var link in links)
        {
            _cache[link.LinkPath] = link;
        }
    }

    public Task<bool> HasExistingIndexAsync()
    {
        return Task.FromResult(_cache.Count > 0);
    }

    public Task UpsertAsync(SymlinkEntry entry)
    {
        _cache[entry.LinkPath] = entry;
        _dirtyKeys.TryAdd(entry.LinkPath, 0);
        return Task.CompletedTask;
    }

    public Task<List<SymlinkEntry>> GetAllAsync()
    {
        return Task.FromResult(_cache.Values.ToList());
    }

    public Task<List<ScanDirectoryConfig>> GetScanDirectoriesAsync()
    {
        return _db.LoadScanDirectoriesAsync();
    }

    public Task SaveScanDirectoriesAsync(List<ScanDirectoryConfig> dirs)
    {
        return _db.SaveScanDirectoriesAsync(dirs);
    }

    public async Task RebuildIndexAsync()
    {
        _cache.Clear();
        _dirtyKeys.Clear();
        await _db.ClearIndexAsync();
    }

    public CancellationTokenSource StartBatchPersist(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(30000, ct);
                    var snapshot = Interlocked.Exchange(ref _dirtyKeys, new ConcurrentDictionary<string, byte>());
                    if (snapshot.Count > 0)
                    {
                        var entries = snapshot.Keys.Select(k => _cache[k]).ToList();
                        await _db.BatchUpsertLinksAsync(entries);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                var snapshot = Interlocked.Exchange(ref _dirtyKeys, new ConcurrentDictionary<string, byte>());
                if (snapshot.Count > 0)
                {
                    var entries = snapshot.Keys.Select(k => _cache[k]).ToList();
                    await _db.BatchUpsertLinksAsync(entries);
                }
            }
        }, ct);

        return new CancellationTokenSource();
    }
}

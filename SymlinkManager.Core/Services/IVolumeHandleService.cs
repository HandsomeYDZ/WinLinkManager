namespace SymlinkManager.Core.Services;

public interface IVolumeHandleService : IDisposable
{
    void OpenVolume(string volumePath = @"\\.\C:");
    SafeHandleWrapper GetHandle();
}

public class SafeHandleWrapper
{
    public nint Handle { get; set; }
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

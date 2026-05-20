using Microsoft.Win32.SafeHandles;
using SymlinkManager.Core.Native;

namespace SymlinkManager.Core.Services;

public class VolumeHandleService : IVolumeHandleService
{
    private SafeFileHandle? _volumeHandle;

    public SafeHandleWrapper Wrapper { get; } = new();

    public void OpenVolume(string volumePath = @"\\.\C:")
    {
        _volumeHandle = NtfsNative.CreateFileW(
            volumePath,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE | NtfsNative.FILE_SHARE_DELETE,
            nint.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            nint.Zero);

        Wrapper.Handle = _volumeHandle?.DangerousGetHandle() ?? nint.Zero;
    }

    public SafeHandleWrapper GetHandle() => Wrapper;

    public void Dispose()
    {
        _volumeHandle?.Dispose();
        _volumeHandle = null;
        Wrapper.Handle = nint.Zero;
    }
}

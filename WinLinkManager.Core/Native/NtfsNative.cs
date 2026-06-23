using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinLinkManager.Core.Native;

public static class NtfsNative
{
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    // SymbolicLinkReparseBuffer.Flags uses bit 0 to indicate a relative target.
    // This is different from CreateSymbolicLinkW's dwFlags, where bit 0 means
    // that the link itself points to a directory.
    public const uint SYMLINK_FLAG_RELATIVE = 0x1;

    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_REPARSE_POINT_CHANGE = 0x00100000;
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_CLOSE = 0x80000000;

    // File access and sharing constants
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_READ_EA = 0x0008;
    public const uint VOLUME_NAME_DOS = 0x0;

    public const int MAX_REPARSE_DATA_BUFFER_SIZE = 16384;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USN_RECORD_V3
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V1
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        nint lpInBuffer, uint nInBufferSize,
        nint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, nint lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateSymbolicLinkW(
        string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

    /// <summary>
    /// Reads the substitute name from a REPARSE_DATA_BUFFER.
    /// Works for both symlink (IO_REPARSE_TAG_SYMLINK) and junction/mount-point (IO_REPARSE_TAG_MOUNT_POINT).
    /// </summary>
    public static string ReadSubstituteName(IntPtr reparseBuffer, uint reparseTag)
    {
        // Fixed header: ReparseTag(4) + ReparseDataLength(2) + Reserved(2) = 8 bytes
        // Union starts at offset 8.
        bool isSymlink = reparseTag == IO_REPARSE_TAG_SYMLINK;

        // SymbolicLinkReparseBuffer: SubstituteNameOffset(2) + SubstituteNameLength(2) +
        //   PrintNameOffset(2) + PrintNameLength(2) + Flags(4) = 12 bytes before PathBuffer
        //   PathBuffer starts at offset 8+12 = 20
        // MountPointReparseBuffer: SubstituteNameOffset(2) + SubstituteNameLength(2) +
        //   PrintNameOffset(2) + PrintNameLength(2) = 8 bytes before PathBuffer
        //   PathBuffer starts at offset 8+8 = 16

        int pathBufferOffset = isSymlink ? 20 : 16;
        ushort nameOffset = (ushort)Marshal.ReadInt16(reparseBuffer, 8);  // SubstituteNameOffset
        ushort nameLength = (ushort)Marshal.ReadInt16(reparseBuffer, 10); // SubstituteNameLength

        IntPtr namePtr = IntPtr.Add(reparseBuffer, pathBufferOffset + nameOffset);
        // nameLength is in bytes, each WCHAR is 2 bytes
        int charCount = nameLength / 2;
        return Marshal.PtrToStringUni(namePtr, charCount) ?? string.Empty;
    }

    /// <summary>
    /// Resolves the final target path for a reparse point by opening it without
    /// FILE_FLAG_OPEN_REPARSE_POINT, allowing the filesystem to follow the link.
    /// </summary>
    public static string? ResolveTarget(SafeFileHandle handle)
    {
        // First call to get buffer size (returns required character count including null terminator)
        uint bufSize = GetFinalPathNameByHandle(handle, IntPtr.Zero, 0, VOLUME_NAME_DOS);
        if (bufSize == 0) return null;

        nint buffer = Marshal.AllocHGlobal((int)(bufSize * 2));
        try
        {
            uint result = GetFinalPathNameByHandle(handle, buffer, bufSize, VOLUME_NAME_DOS);
            if (result == 0) return null;

            string path = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return path.StartsWith(@"\\?\") ? path.Substring(4) : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string NtToWin32Path(string ntPath)
    {
        if (ntPath.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + ntPath.Substring(8);
        if (ntPath.StartsWith(@"\??\"))
            return ntPath.Substring(4);
        if (ntPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + ntPath.Substring(8);
        if (ntPath.StartsWith(@"\\?\"))
            return ntPath.Substring(4);
        if (ntPath.StartsWith(@"\Device\HarddiskVolume"))
        {
            // Simplified: assume volume 1 maps to C:
            var rest = ntPath.Substring("\\Device\\HarddiskVolume".Length);
            var volNumStr = "";
            foreach (var c in rest)
            {
                if (char.IsDigit(c)) volNumStr += c;
                else break;
            }
            var remainder = rest.Substring(volNumStr.Length);
            return $"C:{remainder}";
        }
        return ntPath;
    }
}

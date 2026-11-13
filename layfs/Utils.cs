using System.Runtime.InteropServices;

using Fsp;

using FileInfo = Fsp.Interop.FileInfo;

namespace layfs;

internal static class Utils
{
    public const int AllocationUnit = 4096;

    public static void ThrowIoExceptionWithHResult(int hResult) =>
        throw new IOException(null, hResult);

    public static void ThrowIoExceptionWithWin32(int error) =>
        ThrowIoExceptionWithHResult(unchecked((int)(0x80070000 | error)));

    public static void ThrowIoExceptionWithNtStatus(int status) =>
        ThrowIoExceptionWithWin32((int)FileSystemBase.Win32FromNtStatus(status));

    public static int GetFileInfoFromFileStream(FileStream stream, out FileInfo fileInfo)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var info))
            ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());

        fileInfo.FileAttributes = info.dwFileAttributes;
        fileInfo.ReparseTag = 0;
        fileInfo.FileSize = (ulong)stream.Length;
        fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        fileInfo.CreationTime = info.ftCreationTime;
        fileInfo.LastAccessTime = info.ftLastAccessTime;
        fileInfo.LastWriteTime = info.ftLastWriteTime;
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public static int GetFileInfoFromFileSystemInfo(FileSystemInfo info, out FileInfo fileInfo)
    {
        fileInfo.FileAttributes = (uint)info.Attributes;
        fileInfo.ReparseTag = 0;
        fileInfo.FileSize = info is System.IO.FileInfo file ? (ulong)file.Length : 0;
        fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        fileInfo.CreationTime = (ulong)info.CreationTimeUtc.ToFileTimeUtc();
        fileInfo.LastAccessTime = (ulong)info.LastAccessTimeUtc.ToFileTimeUtc();
        fileInfo.LastWriteTime = (ulong)info.LastWriteTimeUtc.ToFileTimeUtc();
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public static int GetFileInfoFromDirectoryInfo(DirectoryInfo dirInfo, out FileInfo fileInfo) =>
        GetFileInfoFromFileSystemInfo(dirInfo, out fileInfo);

    public static void SetBasicInfoToFileStream(
        FileStream stream,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime)
    {
        if (0 == fileAttributes)
            fileAttributes = (uint)System.IO.FileAttributes.Normal;

        var info = default(FILE_BASIC_INFO);
        if (uint.MaxValue != fileAttributes)
            info.FileAttributes = fileAttributes;
        if (0 != creationTime)
            info.CreationTime = creationTime;
        if (0 != lastAccessTime)
            info.LastAccessTime = lastAccessTime;
        if (0 != lastWriteTime)
            info.LastWriteTime = lastWriteTime;
        if (!SetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(),
            0/*FileBasicInfo*/, ref info, (uint)Marshal.SizeOf(info)))
            ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
    }

    public static void SetBasicInfoToDirectoryInfo(
        DirectoryInfo dirInfo,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime)
    {
        if (0 == fileAttributes)
            fileAttributes = (uint)System.IO.FileAttributes.Normal;

        if (uint.MaxValue != fileAttributes)
            dirInfo.Attributes = (System.IO.FileAttributes)fileAttributes;
        if (0 != creationTime)
            dirInfo.CreationTimeUtc = DateTime.FromFileTimeUtc((long)creationTime);
        if (0 != lastAccessTime)
            dirInfo.LastAccessTimeUtc = DateTime.FromFileTimeUtc((long)lastAccessTime);
        if (0 != lastWriteTime)
            dirInfo.LastWriteTimeUtc = DateTime.FromFileTimeUtc((long)lastWriteTime);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public ulong ftCreationTime;
        public ulong ftLastAccessTime;
        public ulong ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public ulong ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        IntPtr hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        IntPtr hFile,
        int FileInformationClass,
        ref FILE_BASIC_INFO lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool MoveFileExW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpExistingFileName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpNewFileName,
        uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetFileSecurityW(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        int securityInformation,
        byte[] securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetKernelObjectSecurity(
        IntPtr handle,
        int securityInformation,
        byte[] securityDescriptor);
}

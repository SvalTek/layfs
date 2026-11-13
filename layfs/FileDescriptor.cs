using System.Runtime.InteropServices;
using System.Security.AccessControl;

using FileInfo = Fsp.Interop.FileInfo;

namespace layfs;

internal sealed class FileDescriptor
{
    public FileDescriptor(string fileName, FileStream stream, bool writeable)
    {
        FileName = fileName;

        if (writeable)
            WriteStream = stream;
        else
            ReadOnlyStream = stream;
    }

    public FileDescriptor(string fileName, DirectoryInfo dirInfo, bool writeable)
    {
        FileName = fileName;

        if (writeable)
            WriteDirInfo = dirInfo;
        else
            ReadOnlyDirInfo = dirInfo;
    }

    public string FileName { get; }

    public FileStream? WriteStream { get; set; }

    public FileStream? ReadOnlyStream { get; set; }

    public DirectoryInfo? WriteDirInfo { get; set; }

    public DirectoryInfo? ReadOnlyDirInfo { get; set; }

    /// <summary>Cached union of this directory's entries, built on first enumeration.</summary>
    public KeyValuePair<string, FileSystemInfo>[]? DirectoryEntries { get; set; }

    public bool IsFile() =>
        WriteStream is not null || (WriteDirInfo is null && ReadOnlyStream is not null);

    public bool IsDirectory() =>
        WriteDirInfo is not null || (WriteStream is null && ReadOnlyDirInfo is not null);

    public bool IsWriteable() =>
        WriteStream is not null || WriteDirInfo is not null;

    public bool IsReadOnly() => !IsWriteable();

    public void SetFileAttributes(uint fileAttributes) =>
        SetBasicInfo(fileAttributes, 0, 0, 0);

    public int GetFileInfo(out FileInfo fileInfo)
    {
        if (IsWriteable())
        {
            return IsFile()
                ? Utils.GetFileInfoFromFileStream(WriteStream!, out fileInfo)
                : Utils.GetFileInfoFromDirectoryInfo(WriteDirInfo!, out fileInfo);
        }

        return IsFile()
            ? Utils.GetFileInfoFromFileStream(ReadOnlyStream!, out fileInfo)
            : Utils.GetFileInfoFromDirectoryInfo(ReadOnlyDirInfo!, out fileInfo);
    }

    public void Dispose()
    {
        WriteStream?.Dispose();
        ReadOnlyStream?.Dispose();
    }

    public void Flush()
    {
        WriteStream?.Flush(true);
        ReadOnlyStream?.Flush(true);
    }

    public void SetBasicInfo(
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime)
    {
        // Metadata changes on a read-only layer handle are intentionally ignored;
        // callers promote the file to the write layer first.
        if (!IsWriteable())
            return;

        if (0 == fileAttributes)
            fileAttributes = (uint)System.IO.FileAttributes.Normal;

        if (IsFile())
            Utils.SetBasicInfoToFileStream(WriteStream!, fileAttributes, creationTime, lastAccessTime, lastWriteTime);
        else
            Utils.SetBasicInfoToDirectoryInfo(WriteDirInfo!, fileAttributes, creationTime, lastAccessTime, lastWriteTime);
    }

    public uint GetFileAttributes()
    {
        GetFileInfo(out var fileInfo);
        return fileInfo.FileAttributes;
    }

    public void Close() => Dispose();

    public FileStream GetReadStream() => IsWriteable() ? WriteStream! : ReadOnlyStream!;

    public byte[] GetSecurityDescriptor()
    {
        if (IsWriteable())
        {
            return IsFile()
                ? WriteStream!.GetAccessControl().GetSecurityDescriptorBinaryForm()
                : WriteDirInfo!.GetAccessControl().GetSecurityDescriptorBinaryForm();
        }

        return IsFile()
            ? ReadOnlyStream!.GetAccessControl().GetSecurityDescriptorBinaryForm()
            : ReadOnlyDirInfo!.GetAccessControl().GetSecurityDescriptorBinaryForm();
    }

    public void SetSecurityDescriptor(AccessControlSections sections, byte[] securityDescriptor)
    {
        if (!IsWriteable())
            return;

        var securityInformation = 0;
        if (0 != (sections & AccessControlSections.Owner))
            securityInformation |= 1/*OWNER_SECURITY_INFORMATION*/;
        if (0 != (sections & AccessControlSections.Group))
            securityInformation |= 2/*GROUP_SECURITY_INFORMATION*/;
        if (0 != (sections & AccessControlSections.Access))
            securityInformation |= 4/*DACL_SECURITY_INFORMATION*/;
        if (0 != (sections & AccessControlSections.Audit))
            securityInformation |= 8/*SACL_SECURITY_INFORMATION*/;

        if (IsFile())
        {
            if (!Utils.SetKernelObjectSecurity(WriteStream!.SafeFileHandle.DangerousGetHandle(),
                securityInformation, securityDescriptor))
                Utils.ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
        }
        else
        {
            if (!Utils.SetFileSecurityW(WriteDirInfo!.FullName,
                securityInformation, securityDescriptor))
                Utils.ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
        }
    }
}

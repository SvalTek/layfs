using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

using Fsp;

using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace layfs;

/// <summary>
/// Union of a writable layer (<c>-w</c>) over a read-only layer (<c>-r</c>).
/// Files opened from the read-only layer are copied into the writable layer on first write.
/// </summary>
internal sealed class LayeredFileSystem : FileSystemBase
{
    private readonly string _readOnlyPath;
    private readonly string _writePath;

    public LayeredFileSystem(string writePath, string readOnlyPath)
    {
        _writePath = NormalizePath(writePath);
        _readOnlyPath = NormalizePath(readOnlyPath);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool FileOrDirectoryExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private string GetWritePath(string fileName) => _writePath + fileName;

    private string GetReadOnlyPath(string fileName) => _readOnlyPath + fileName;

    /// <summary>
    /// Modern .NET no longer has a <see cref="FileStream"/> constructor that takes
    /// <see cref="FileSystemRights"/>, so collapse the requested access mask down to the
    /// <see cref="FileAccess"/> it implies.
    /// </summary>
    private static FileAccess ToFileAccess(uint grantedAccess)
    {
        const FileSystemRights writeRights =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        const uint GenericWrite = 0x40000000;
        const uint GenericAll = 0x10000000;

        var rights = (FileSystemRights)grantedAccess;

        if (0 != (grantedAccess & (GenericAll | GenericWrite)))
            return FileAccess.ReadWrite;

        return 0 != (rights & writeRights) ? FileAccess.ReadWrite : FileAccess.Read;
    }

    public override int ExceptionHandler(Exception ex)
    {
        DebugLog.Trace("!! {0}: {1}\n{2}", ex.GetType().Name, ex.Message, ex.StackTrace);

        var hResult = ex.HResult;
        if (0x80070000 == (hResult & 0xFFFF0000))
            return NtStatusFromWin32((uint)hResult & 0xFFFF);

        return STATUS_UNEXPECTED_IO_ERROR;
    }

    /// <summary>
    /// The historical binding's <c>Create</c> has a different shape than the one this
    /// WinFsp build dispatches, so trace both entry points.
    /// </summary>
    public override int CreateEx(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[]? securityDescriptor,
        ulong allocationSize,
        IntPtr extraBuffer,
        uint extraLength,
        bool extraBufferIsReparsePoint,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        DebugLog.Trace("CreateEx ENTER {0} opts={1} access={2} attrs={3} alloc={4}",
            fileName, DebugLog.Hex(createOptions), DebugLog.Hex(grantedAccess), DebugLog.Hex(fileAttributes), allocationSize);

        var status = base.CreateEx(
            fileName,
            createOptions,
            grantedAccess,
            fileAttributes,
            securityDescriptor,
            allocationSize,
            extraBuffer,
            extraLength,
            extraBufferIsReparsePoint,
            out fileNode,
            out fileDesc,
            out fileInfo,
            out normalizedName);

        DebugLog.Trace("CreateEx {0} -> {1}", fileName, DebugLog.Hex(status));
        return status;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = Utils.AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = true;
        host.VolumeCreationTime = (ulong)File.GetCreationTimeUtc(_writePath).ToFileTimeUtc();
        host.VolumeSerialNumber = 0;

        DebugLog.Trace(
            "Init: write={0} read={1} sectorSize={2} sectorsPerUnit={3} maxComponentLength={4} "
            + "fileInfoTimeout={5} caseSensitive={6} casePreserved={7} unicode={8} persistentAcls={9} "
            + "postCleanupWhenModifiedOnly={10} passQueryDirectoryPattern={11} volumeSerial={12}",
            _writePath,
            _readOnlyPath,
            host.SectorSize,
            host.SectorsPerAllocationUnit,
            host.MaxComponentLength,
            host.FileInfoTimeout,
            host.CaseSensitiveSearch,
            host.CasePreservedNames,
            host.UnicodeOnDisk,
            host.PersistentAcls,
            host.PostCleanupWhenModifiedOnly,
            host.PassQueryDirectoryPattern,
            host.VolumeSerialNumber);

        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        try
        {
            var info = new DriveInfo(_writePath);
            volumeInfo.TotalSize = (ulong)info.TotalSize;
            volumeInfo.FreeSize = (ulong)info.TotalFreeSpace;
        }
        catch (ArgumentException)
        {
            // DriveInfo only supports drives and does not support UNC paths.
            // It would be better to use GetDiskFreeSpaceEx here.
        }

        DebugLog.Trace("GetVolumeInfo -> total={0} free={1}", volumeInfo.TotalSize, volumeInfo.FreeSize);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[]? securityDescriptor)
    {
        fileAttributes = 0;

        var writePath = GetWritePath(fileName);
        var layer = "write";
        var exists = FileOrDirectoryExists(writePath);

        if (!exists)
        {
            layer = "read";
            writePath = GetReadOnlyPath(fileName);
            exists = FileOrDirectoryExists(writePath);
        }

        // Reporting success for a name that is in neither layer makes WinFsp believe the
        // entry exists (attributes surface as 0xffffffff), so a create is never dispatched.
        if (!exists)
        {
            DebugLog.Trace("GetSecurityByName {0} -> OBJECT_NAME_NOT_FOUND", fileName);
            Utils.ThrowIoExceptionWithNtStatus(STATUS_OBJECT_NAME_NOT_FOUND);
        }

        var info = new System.IO.FileInfo(writePath);
        fileAttributes = (uint)info.Attributes;

        if (null != securityDescriptor)
            securityDescriptor = info.GetAccessControl().GetSecurityDescriptorBinaryForm();

        DebugLog.Trace("GetSecurityByName {0} layer={1} attrs={2} sd={3}",
            fileName, layer, DebugLog.Hex(fileAttributes), null != securityDescriptor);

        return STATUS_SUCCESS;
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[]? securityDescriptor,
        ulong allocationSize,
        out object? fileNode,
        out object? fileDesc0,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = default;
        normalizedName = default;

        DebugLog.Trace("Create ENTER {0} opts={1} access={2} attrs={3} alloc={4} sd={5} -> write path {6}",
            fileName, DebugLog.Hex(createOptions), DebugLog.Hex(grantedAccess), DebugLog.Hex(fileAttributes), allocationSize, null != securityDescriptor, GetWritePath(fileName));

        FileDescriptor? fileDesc = null;
        try
        {
            var path = GetWritePath(fileName);

            if (0 == (createOptions & FILE_DIRECTORY_FILE))
            {
                const FileShare share = FileShare.Read | FileShare.Write | FileShare.Delete;

                FileStream stream;
                if (null == securityDescriptor)
                {
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, share, 4096, FileOptions.None);
                }
                else
                {
                    var security = new FileSecurity();
                    security.SetSecurityDescriptorBinaryForm(securityDescriptor);

                    stream = new System.IO.FileInfo(path).Create(
                        FileMode.CreateNew,
                        (FileSystemRights)grantedAccess | FileSystemRights.WriteAttributes,
                        share,
                        4096,
                        FileOptions.None,
                        security);
                }

                fileDesc = new FileDescriptor(fileName, stream, true);
                fileDesc.SetFileAttributes(fileAttributes | (uint)System.IO.FileAttributes.Archive);
            }
            else
            {
                if (Directory.Exists(path))
                    Utils.ThrowIoExceptionWithNtStatus(STATUS_OBJECT_NAME_COLLISION);

                var dirInfo = new DirectoryInfo(path);
                if (null == securityDescriptor)
                {
                    dirInfo.Create();
                }
                else
                {
                    var security = new DirectorySecurity();
                    security.SetSecurityDescriptorBinaryForm(securityDescriptor);
                    dirInfo.Create(security);
                }

                fileDesc = new FileDescriptor(fileName, dirInfo, true);
                fileDesc.SetFileAttributes(fileAttributes);
            }

            fileDesc0 = fileDesc;
            return fileDesc.GetFileInfo(out fileInfo);
        }
        catch (Exception ex)
        {
            DebugLog.Trace("Create FAILED {0}: {1}", fileName, ex);
            fileDesc?.Dispose();
            throw;
        }
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object? fileNode,
        out object? fileDesc0,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = default;
        normalizedName = default;

        DebugLog.Trace("Open ENTER {0} opts={1} access={2}",
            fileName, DebugLog.Hex(createOptions), DebugLog.Hex(grantedAccess));

        FileDescriptor? fileDesc = null;

        try
        {
            var path = GetWritePath(fileName);

            if (Directory.Exists(path))
            {
                fileDesc = new FileDescriptor(fileName, new DirectoryInfo(path), true);

                path = GetReadOnlyPath(fileName);
                if (Directory.Exists(path))
                    fileDesc.ReadOnlyDirInfo = new DirectoryInfo(path);
            }
            else if (File.Exists(path))
            {
                fileDesc = new FileDescriptor(
                    fileName,
                    new FileStream(
                        path,
                        FileMode.Open,
                        ToFileAccess(grantedAccess),
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        4096,
                        FileOptions.None),
                    true);
            }
            else
            {
                path = GetReadOnlyPath(fileName);
                if (Directory.Exists(path))
                {
                    fileDesc = new FileDescriptor(fileName, new DirectoryInfo(path), false);
                }
                else if (File.Exists(path))
                {
                    fileDesc = new FileDescriptor(
                        fileName,
                        new FileStream(
                            path,
                            FileMode.Open,
                            ToFileAccess(grantedAccess),
                            FileShare.Read | FileShare.Write | FileShare.Delete,
                            4096,
                            FileOptions.None),
                        false);
                }
                else
                {
                    DebugLog.Trace("Open NOT FOUND {0} (write={1} read={2})", fileName, _writePath + fileName, _readOnlyPath + fileName);
                    throw new FileNotFoundException();
                }
            }

            fileDesc0 = fileDesc;
            DebugLog.Trace("Open OK {0} writeable={1} dir={2}", fileName, fileDesc.IsWriteable(), fileDesc.IsDirectory());
            return fileDesc.GetFileInfo(out fileInfo);
        }
        catch
        {
            fileDesc?.Dispose();
            throw;
        }
    }

    private void MakeWriteable(FileDescriptor fileDesc)
    {
        if (fileDesc.IsWriteable())
            return;

        DebugLog.Trace("MakeWriteable {0}", fileDesc.FileName);

        var parentPath = Path.GetDirectoryName(GetWritePath(fileDesc.FileName));
        if (null != parentPath && !Directory.Exists(parentPath))
            Directory.CreateDirectory(parentPath);

        var path = GetWritePath(fileDesc.FileName);

        File.Copy(GetReadOnlyPath(fileDesc.FileName), path, true);

        fileDesc.WriteStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            4096,
            0);
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc0,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        DebugLog.Trace("Overwrite {0} replace={1} attrs={2} alloc={3}",
            fileDesc.FileName, replaceFileAttributes, DebugLog.Hex(fileAttributes), allocationSize);

        MakeWriteable(fileDesc);

        if (replaceFileAttributes)
            fileDesc.SetFileAttributes(fileAttributes | (uint)System.IO.FileAttributes.Archive);
        else if (0 != fileAttributes)
            fileDesc.SetFileAttributes(fileDesc.GetFileAttributes() | fileAttributes | (uint)System.IO.FileAttributes.Archive);

        fileDesc.WriteStream!.SetLength(0);

        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override void Cleanup(
        object fileNode,
        object fileDesc0,
        string? fileName,
        uint flags)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        DebugLog.Trace("Cleanup {0} flags={1} writeable={2}", fileName ?? fileDesc.FileName, DebugLog.Hex(flags), fileDesc.IsWriteable());

        if (0 != (flags & CleanupDelete))
        {
            var path = GetWritePath(fileName ?? fileDesc.FileName);
            var isDirectory = fileDesc.IsDirectory();

            DebugLog.Trace("Cleanup DELETE {0} path={1} dir={2}", fileDesc.FileName, path, isDirectory);

            // The handle does not grant delete access, and the stream keeps the file open,
            // so release it before deleting by path.
            fileDesc.Dispose();

            if (isDirectory)
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
        }
    }

    public override void Close(object fileNode, object fileDesc0)
    {
        var fileDesc = (FileDescriptor)fileDesc0;
        DebugLog.Trace("Close {0}", fileDesc.FileName);
        fileDesc.Close();
    }

    public override int Read(
        object fileNode,
        object fileDesc0,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        var stream = fileDesc.GetReadStream();

        DebugLog.Trace("Read {0} offset={1} length={2} streamLen={3}", fileDesc.FileName, offset, length, stream.Length);

        if (offset >= (ulong)stream.Length)
            Utils.ThrowIoExceptionWithNtStatus(STATUS_END_OF_FILE);

        var bytes = new byte[length];

        stream.Seek((long)offset, SeekOrigin.Begin);

        bytesTransferred = (uint)stream.Read(bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, buffer, (int)bytesTransferred);
        return STATUS_SUCCESS;
    }

    public override int Write(
        object fileNode,
        object fileDesc0,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        DebugLog.Trace("Write {0} offset={1} length={2} toEof={3} constrained={4} writeable={5}",
            fileDesc.FileName, offset, length, writeToEndOfFile, constrainedIo, fileDesc.IsWriteable());

        MakeWriteable(fileDesc);

        var stream = fileDesc.WriteStream!;

        if (constrainedIo)
        {
            if (offset >= (ulong)stream.Length)
            {
                bytesTransferred = default;
                fileInfo = default;
                return STATUS_SUCCESS;
            }

            if (offset + length > (ulong)stream.Length)
                length = (uint)((ulong)stream.Length - offset);
        }

        var bytes = new byte[length];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);

        if (!writeToEndOfFile)
            stream.Seek((long)offset, SeekOrigin.Begin);

        stream.Write(bytes, 0, bytes.Length);
        bytesTransferred = length;

        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override int Flush(object fileNode, object? fileDesc0, out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor?)fileDesc0;
        if (null == fileDesc)
        {
            // We do not flush the whole volume.
            DebugLog.Trace("Flush <volume>");
            fileInfo = default;
            return STATUS_SUCCESS;
        }

        DebugLog.Trace("Flush {0}", fileDesc.FileName);
        fileDesc.Flush();

        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override int GetFileInfo(object fileNode, object fileDesc0, out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;
        DebugLog.Trace("GetFileInfo {0} writeable={1}", fileDesc.FileName, fileDesc.IsWriteable());
        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc0,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;
        DebugLog.Trace("SetBasicInfo {0} attrs={1} ctime={2} atime={3} mtime={4}",
            fileDesc.FileName, DebugLog.Hex(fileAttributes), creationTime, lastAccessTime, lastWriteTime);
        MakeWriteable(fileDesc);
        fileDesc.SetBasicInfo(fileAttributes, creationTime, lastAccessTime, lastWriteTime);

        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc0,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;
        DebugLog.Trace("SetFileSize {0} newSize={1} allocation={2}", fileDesc.FileName, newSize, setAllocationSize);
        MakeWriteable(fileDesc);

        if (!setAllocationSize || (ulong)fileDesc.WriteStream!.Length > newSize)
        {
            // Allocation size is not supported, but a smaller allocation size must still truncate.
            fileDesc.WriteStream!.SetLength((long)newSize);
        }

        return fileDesc.GetFileInfo(out fileInfo);
    }

    public override int CanDelete(object fileNode, object fileDesc0, string fileName)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        // Deletions only ever touch the writable layer. Without whiteout support an entry
        // that only exists in a read-only layer cannot be shadowed, so it is not deletable.
        if (!fileDesc.IsWriteable())
        {
            DebugLog.Trace("CanDelete {0} -> ACCESS_DENIED (read-only layer)", fileDesc.FileName);
            return STATUS_ACCESS_DENIED;
        }

        if (fileDesc.IsDirectory() && !IsEmptyInUnion(fileDesc.FileName))
        {
            DebugLog.Trace("CanDelete {0} -> DIRECTORY_NOT_EMPTY", fileDesc.FileName);
            return STATUS_DIRECTORY_NOT_EMPTY;
        }

        DebugLog.Trace("CanDelete {0} -> OK", fileDesc.FileName);
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Emptiness is a property of the merged view: a directory that is empty on the write
    /// layer can still show lower-layer children, and deleting it would expose the lower copy.
    /// </summary>
    private bool IsEmptyInUnion(string fileName)
    {
        var writeDir = new DirectoryInfo(GetWritePath(fileName));
        if (writeDir.Exists && writeDir.EnumerateFileSystemInfos().Any())
            return false;

        var readDir = new DirectoryInfo(GetReadOnlyPath(fileName));
        return !readDir.Exists || !readDir.EnumerateFileSystemInfos().Any();
    }

    public override int Rename(
        object fileNode,
        object fileDesc0,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        // Only the writable layer supports renames; a lower-layer entry cannot be shadowed.
        var path = GetWritePath(fileName);

        DebugLog.Trace("Rename {0} -> {1} replace={2} writeExists={3}", fileName, newFileName, replaceIfExists, FileOrDirectoryExists(path));

        if (!FileOrDirectoryExists(path))
            return STATUS_ACCESS_DENIED;

        if (!Utils.MoveFileExW(path, GetWritePath(newFileName), replaceIfExists ? 1U/*MOVEFILE_REPLACE_EXISTING*/ : 0))
            Utils.ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());

        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc0, ref byte[] securityDescriptor)
    {
        var fileDesc = (FileDescriptor)fileDesc0;
        securityDescriptor = fileDesc.GetSecurityDescriptor();
        DebugLog.Trace("GetSecurity {0} bytes={1}", fileDesc.FileName, securityDescriptor.Length);
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(
        object fileNode,
        object fileDesc0,
        AccessControlSections sections,
        byte[] securityDescriptor)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        DebugLog.Trace("SetSecurity {0} sections={1} bytes={2}", fileDesc.FileName, sections, securityDescriptor.Length);

        MakeWriteable(fileDesc);
        fileDesc.SetSecurityDescriptor(sections, securityDescriptor);

        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc0,
        string? pattern,
        string? marker,
        ref object? context,
        out string? fileName,
        out FileInfo fileInfo)
    {
        var fileDesc = (FileDescriptor)fileDesc0;

        DebugLog.Trace("ReadDirectoryEntry {0} pattern={1} marker={2} context={3} cached={4}",
            fileDesc.FileName, pattern ?? "<all>", marker ?? "-", context?.ToString() ?? "-", null != fileDesc.DirectoryEntries);

        var entries = fileDesc.DirectoryEntries;
        if (null == entries)
        {
            var findPattern = pattern?.Replace('<', '*').Replace('>', '?').Replace('"', '.') ?? "*";

            var list = new SortedList<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);

            // The parent entries come from whichever layer the open resolved against.
            var primaryDir = fileDesc.IsWriteable() ? fileDesc.WriteDirInfo : fileDesc.ReadOnlyDirInfo;
            if (null != primaryDir?.Parent)
            {
                list.Add(".", primaryDir);
                list.Add("..", primaryDir.Parent);
            }

            AddLayerEntries(list, fileDesc.WriteDirInfo);
            AddLayerEntries(list, fileDesc.ReadOnlyDirInfo);

            // PassQueryDirectoryPattern is enabled, so the pattern is ours to honor.
            var matched = list
                .Where(entry => FileSystemName.MatchesSimpleExpression(findPattern, entry.Key, ignoreCase: true))
                .ToArray();

            DebugLog.Trace("  pattern {0} matched {1} of {2} entries for {3}: {4}",
                findPattern, matched.Length, list.Count, fileDesc.FileName, string.Join(", ", matched.Select(entry => entry.Key)));

            fileDesc.DirectoryEntries = matched;
            entries = matched;
        }

        int index;
        if (null == context)
        {
            index = 0;
            if (null != marker)
            {
                index = Array.BinarySearch(
                    entries,
                    new KeyValuePair<string, FileSystemInfo>(marker, null!),
                    DirectoryEntryComparer.Instance);
                index = 0 <= index ? index + 1 : ~index;
            }
        }
        else
        {
            index = (int)context;
        }

        if (entries.Length > index)
        {
            context = index + 1;
            fileName = entries[index].Key;
            Utils.GetFileInfoFromFileSystemInfo(entries[index].Value, out fileInfo);
            return true;
        }

        DebugLog.Trace("ReadDirectoryEntry {0} -> end at index {1}", fileDesc.FileName, index);
        fileName = default;
        fileInfo = default;
        return false;
    }

    private static void AddLayerEntries(
        SortedList<string, FileSystemInfo> list,
        DirectoryInfo? dirInfo)
    {
        if (null == dirInfo)
            return;

        foreach (var info in dirInfo.EnumerateFileSystemInfos())
        {
            if (!list.ContainsKey(info.Name))
                list.Add(info.Name, info);
        }
    }

    private sealed class DirectoryEntryComparer : IComparer<KeyValuePair<string, FileSystemInfo>>
    {
        public static readonly DirectoryEntryComparer Instance = new();

        public int Compare(KeyValuePair<string, FileSystemInfo> x, KeyValuePair<string, FileSystemInfo> y) =>
            StringComparer.OrdinalIgnoreCase.Compare(x.Key, y.Key);
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct USN_JOURNAL_DATA_V0
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

[StructLayout(LayoutKind.Sequential)]
public struct MFT_ENUM_DATA_V0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

[StructLayout(LayoutKind.Sequential)]
public struct READ_USN_JOURNAL_DATA_V0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
}

[StructLayout(LayoutKind.Sequential)]
public struct CREATE_USN_JOURNAL_DATA
{
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>52 bytes: the FILETIMEs are 4-byte aligned, hence Pack = 4 for the longs that stand in for them.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BY_HANDLE_FILE_INFORMATION
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

/// <summary>USN_RECORD_V2 field offsets (the record is read straight out of the ioctl buffer).</summary>
public static class UsnRecord
{
    public const int RecordLength = 0;
    public const int MajorVersion = 4;
    public const int FileReferenceNumber = 8;
    public const int ParentFileReferenceNumber = 16;
    public const int Usn = 24;
    public const int Reason = 40;
    public const int FileAttributes = 52;
    public const int FileNameLength = 56;
    public const int FileNameOffset = 58;

    public const uint REASON_FILE_CREATE = 0x00000100;
    public const uint REASON_FILE_DELETE = 0x00000200;
    public const uint REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint REASON_BASIC_INFO_CHANGE = 0x00008000;
    public const uint REASON_DATA_OVERWRITE = 0x00000001;
    public const uint REASON_DATA_EXTEND = 0x00000002;
    public const uint REASON_DATA_TRUNCATION = 0x00000004;
    public const uint REASON_CLOSE = 0x80000000;
}

[StructLayout(LayoutKind.Sequential)]
public struct FILE_ID_DESCRIPTOR
{
    public uint Size; // 24
    public int Type; // 0: FileIdType
    public long FileId;
    public long Unused; // the union is 16 bytes (room for a GUID)
}

public static class VolumeNative
{
    private const string Dll = "kernel32.dll";

    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint DRIVE_FIXED = 3;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    /// <summary>Offset of MftValidDataLength in NTFS_VOLUME_DATA_BUFFER, and of BytesPerFileRecordSegment.</summary>
    public const int NtfsMftValidDataLength = 56, NtfsBytesPerFileRecordSegment = 48, NtfsVolumeDataSize = 96;

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(string rootPath, char[]? volumeName, int volumeNameSize, out uint serialNumber,
        out uint maxComponentLength, out uint fileSystemFlags, [Out] char[] fileSystemName, int fileSystemNameSize);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string rootPath);

    [DllImport(Dll)]
    public static extern uint GetLogicalDrives();

    [DllImport(Dll, SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr security, uint flags);

    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000, THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport(Dll)]
    public static extern IntPtr GetCurrentThread();
}

[StructLayout(LayoutKind.Sequential)]
public struct SERVICE_STATUS
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SERVICE_TABLE_ENTRY
{
    public string? ServiceName;
    public IntPtr ServiceProc;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SERVICE_DESCRIPTION
{
    public string? Description;
}

[StructLayout(LayoutKind.Sequential)]
public struct SERVICE_DELAYED_AUTO_START_INFO
{
    [MarshalAs(UnmanagedType.Bool)] public bool DelayedAutostart;
}

[StructLayout(LayoutKind.Sequential)]
public struct SC_ACTION
{
    public int Type;
    public uint Delay;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SERVICE_FAILURE_ACTIONS
{
    public uint ResetPeriod;
    public string? RebootMsg;
    public string? Command;
    public uint ActionCount;
    public IntPtr Actions;
}

public static class ServiceNative
{
    private const string Dll = "advapi32.dll";

    public const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_CREATE_SERVICE = 0x0002;
    public const uint SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_STATUS = 0x0004,
        SERVICE_START = 0x0010, SERVICE_STOP = 0x0020, DELETE = 0x00010000;
    public const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1, SERVICE_NO_CHANGE = 0xFFFFFFFF;
    public const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5;
    public const uint SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4;
    public const uint SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    public const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2, SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    public const int SC_ACTION_RESTART = 1;

    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_NOT_ACTIVE = 1062, ERROR_SERVICE_MARKED_FOR_DELETE = 1072,
        ERROR_SERVICE_EXISTS = 1073, ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void ServiceMain(uint argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint HandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceDescription(IntPtr service, uint infoLevel, ref SERVICE_DESCRIPTION info);

    [DllImport(Dll, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceDelayedStart(IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceFailureActions(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceW(IntPtr service, int argc, IntPtr argv);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr service);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceCtrlDispatcherW([In] SERVICE_TABLE_ENTRY[] table);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);
}

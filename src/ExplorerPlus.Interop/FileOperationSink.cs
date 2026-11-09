using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

[ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int hrResult);
    [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem? psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem? psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem? psiItem);
    [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem? psiItem, int hrDelete, IShellItem? psiNewlyCreated);
    [PreserveSig] int PreNewItem(uint dwFlags, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
    [PreserveSig] int PostNewItem(uint dwFlags, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
    [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

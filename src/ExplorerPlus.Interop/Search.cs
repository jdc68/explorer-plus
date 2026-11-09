using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

[ComImport, Guid("a0ffbc28-5482-4366-be27-3e81e78e06c2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISearchFolderItemFactory
{
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName);
    [PreserveSig] int SetFolderTypeID(Guid ftid);
    [PreserveSig] int SetFolderLogicalViewMode(int flvm);
    [PreserveSig] int SetIconSize(int iIconSize);
    [PreserveSig] int SetVisibleColumns(uint cVisibleColumns, IntPtr rgKey);
    [PreserveSig] int SetSortColumns(uint cSortColumns, IntPtr rgSortColumns);
    [PreserveSig] int SetGroupColumn(ref PROPERTYKEY keyGroup);
    [PreserveSig] int SetStacks(uint cStackKeys, IntPtr rgStackKeys);
    [PreserveSig] int SetScope(IntPtr psiaScope);
    [PreserveSig] int SetCondition([MarshalAs(UnmanagedType.IUnknown)] object pCondition);
    [PreserveSig] int GetShellItem(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetIDList(out IntPtr ppidl);
}

[ComImport, Guid("A879E3C4-AF77-44fb-8F37-EBD1487CF920"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IQueryParserManager
{
    [PreserveSig] int CreateLoadedParser([MarshalAs(UnmanagedType.LPWStr)] string pszCatalog, ushort langidForKeywords, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppQueryParser);
    [PreserveSig] int InitializeOptions([MarshalAs(UnmanagedType.Bool)] bool fUnderstandNQS, [MarshalAs(UnmanagedType.Bool)] bool fAutoWildCard, IQueryParser pQueryParser);
    [PreserveSig] int SetOption(int option, IntPtr pOptionValue);
}

[ComImport, Guid("2EBDEE67-3505-43f8-9946-EA44ABC8E5B0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IQueryParser
{
    [PreserveSig] int Parse([MarshalAs(UnmanagedType.LPWStr)] string pszInputString, IntPtr pCustomProperties, out IQuerySolution ppSolution);
}

/// <summary>IQuerySolution, including the IConditionFactory methods it derives from (in vtable order).</summary>
[ComImport, Guid("D6EBC66B-8921-4193-AFDD-A1789FB7FF57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IQuerySolution
{
    // IConditionFactory
    [PreserveSig] int MakeNot(IntPtr pcSub, int fSimplify, out IntPtr ppcResult);
    [PreserveSig] int MakeAndOr(int ct, IntPtr peuSubs, int fSimplify, out IntPtr ppcResult);
    [PreserveSig] int MakeLeaf(IntPtr pszPropertyName, int cop, IntPtr pszValueType, IntPtr ppropvar, IntPtr a, IntPtr b, IntPtr c, int fExpand, out IntPtr ppcResult);
    [PreserveSig] int Resolve([MarshalAs(UnmanagedType.IUnknown)] object pc, int sqro, ref SYSTEMTIME pstReferenceTime, [MarshalAs(UnmanagedType.IUnknown)] out object ppcResolved);
    // IQuerySolution
    [PreserveSig] int GetQuery([MarshalAs(UnmanagedType.IUnknown)] out object ppQueryNode, IntPtr ppMainType);
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEMTIME
{
    public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;

    public static SYSTEMTIME Now()
    {
        var n = DateTime.Now;
        return new SYSTEMTIME
        {
            Year = (ushort)n.Year, Month = (ushort)n.Month, DayOfWeek = (ushort)n.DayOfWeek, Day = (ushort)n.Day,
            Hour = (ushort)n.Hour, Minute = (ushort)n.Minute, Second = (ushort)n.Second, Milliseconds = (ushort)n.Millisecond,
        };
    }
}

public static class SearchClsids
{
    public static readonly Guid SearchFolderItemFactory = new("14010e02-bbbd-41f0-88e3-eda371216584");
    public static readonly Guid QueryParserManager = new("5088B39A-29B4-4d9d-8245-4EE289222F66");
    public static readonly Guid IID_IQueryParser = new("2EBDEE67-3505-43f8-9946-EA44ABC8E5B0");
    public const int SQRO_DONT_SPLIT_WORDS = 0x40;
    public const ushort LANG_USER_DEFAULT = 0x0400;
}

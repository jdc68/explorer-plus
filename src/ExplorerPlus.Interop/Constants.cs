namespace ExplorerPlus.Interop;

public enum SIGDN : uint
{
    NORMALDISPLAY = 0,
    PARENTRELATIVEPARSING = 0x80018001,
    DESKTOPABSOLUTEPARSING = 0x80028000,
    PARENTRELATIVEEDITING = 0x80031001,
    DESKTOPABSOLUTEEDITING = 0x8004c000,
    FILESYSPATH = 0x80058000,
    URL = 0x80068000,
    PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
    PARENTRELATIVE = 0x80080001,
}

[Flags]
public enum SFGAO : uint
{
    CANCOPY = 0x1,
    CANMOVE = 0x2,
    CANLINK = 0x4,
    STORAGE = 0x8,
    CANRENAME = 0x10,
    CANDELETE = 0x20,
    HASPROPSHEET = 0x40,
    DROPTARGET = 0x100,
    ENCRYPTED = 0x2000,
    ISSLOW = 0x4000,
    GHOSTED = 0x8000,
    LINK = 0x10000,
    SHARE = 0x20000,
    READONLY = 0x40000,
    HIDDEN = 0x80000,
    NONENUMERATED = 0x100000,
    NEWCONTENT = 0x200000,
    STREAM = 0x400000,
    STORAGEANCESTOR = 0x800000,
    VALIDATE = 0x1000000,
    REMOVABLE = 0x2000000,
    COMPRESSED = 0x4000000,
    BROWSABLE = 0x8000000,
    FILESYSANCESTOR = 0x10000000,
    FOLDER = 0x20000000,
    FILESYSTEM = 0x40000000,
    HASSUBFOLDER = 0x80000000,
}

[Flags]
public enum SHCONTF : uint
{
    FOLDERS = 0x20,
    NONFOLDERS = 0x40,
    INCLUDEHIDDEN = 0x80,
    FASTITEMS = 0x2000,
    FLATLIST = 0x4000,
    ENABLE_ASYNC = 0x8000,
    INCLUDESUPERHIDDEN = 0x10000,
}

[Flags]
public enum SHGFI : uint
{
    ICON = 0x100,
    DISPLAYNAME = 0x200,
    TYPENAME = 0x400,
    ATTRIBUTES = 0x800,
    ICONLOCATION = 0x1000,
    EXETYPE = 0x2000,
    SYSICONINDEX = 0x4000,
    LINKOVERLAY = 0x8000,
    SELECTED = 0x10000,
    ATTR_SPECIFIED = 0x20000,
    LARGEICON = 0x0,
    SMALLICON = 0x1,
    OPENICON = 0x2,
    SHELLICONSIZE = 0x4,
    PIDL = 0x8,
    USEFILEATTRIBUTES = 0x10,
    ADDOVERLAYS = 0x20,
    OVERLAYINDEX = 0x40,
}

[Flags]
public enum SHCNE : int
{
    RENAMEITEM = 0x1,
    CREATE = 0x2,
    DELETE = 0x4,
    MKDIR = 0x8,
    RMDIR = 0x10,
    MEDIAINSERTED = 0x20,
    MEDIAREMOVED = 0x40,
    DRIVEREMOVED = 0x80,
    DRIVEADD = 0x100,
    NETSHARE = 0x200,
    NETUNSHARE = 0x400,
    ATTRIBUTES = 0x800,
    UPDATEDIR = 0x1000,
    UPDATEITEM = 0x2000,
    SERVERDISCONNECT = 0x4000,
    UPDATEIMAGE = 0x8000,
    DRIVEADDGUI = 0x10000,
    RENAMEFOLDER = 0x20000,
    FREESPACE = 0x40000,
    ASSOCCHANGED = 0x8000000,
    ALLEVENTS = 0x7FFFFFFF,
}

public static class SHCNRF
{
    public const int InterruptLevel = 0x1;
    public const int ShellLevel = 0x2;
    public const int RecursiveInterrupt = 0x1000;
    public const int NewDelivery = 0x8000;
}

public static class FOF
{
    public const uint RENAMEONCOLLISION = 0x8;
    public const uint NOCONFIRMATION = 0x10;
    public const uint ALLOWUNDO = 0x40;
    public const uint NOCONFIRMMKDIR = 0x200;
    public const uint WANTNUKEWARNING = 0x4000;
    public const uint FOFX_SHOWELEVATIONPROMPT = 0x40000;
    public const uint FOFX_RECYCLEONDELETE = 0x80000;
    public const uint FOFX_EARLYFAILURE = 0x100000;
    public const uint FOFX_ADDUNDORECORD = 0x20000000;
}

public static class CMF
{
    public const uint NORMAL = 0x0;
    public const uint DEFAULTONLY = 0x1;
    public const uint VERBSONLY = 0x2;
    public const uint EXPLORE = 0x4;
    public const uint NOVERBS = 0x8;
    public const uint CANRENAME = 0x10;
    public const uint NODEFAULT = 0x20;
    public const uint EXTENDEDVERBS = 0x100;
    public const uint ITEMMENU = 0x80;
    public const uint ASYNCVERBSTATE = 0x400;
}

public static class CMIC
{
    public const uint MASK_ASYNCOK = 0x00100000;
    public const uint MASK_UNICODE = 0x00004000;
    public const uint MASK_SHIFT_DOWN = 0x10000000;
    public const uint MASK_CONTROL_DOWN = 0x40000000;
    public const uint MASK_PTINVOKE = 0x20000000;
    public const uint MASK_NOASYNC = 0x00000100;
}

public static class GCS
{
    public const uint VERBW = 0x4;
    public const uint HELPTEXTW = 0x5;
}

public static class TPM
{
    public const uint LEFTALIGN = 0x0;
    public const uint RIGHTBUTTON = 0x2;
    public const uint RETURNCMD = 0x100;
    public const uint NONOTIFY = 0x80;
}

public static class MF
{
    public const uint STRING = 0x0;
    public const uint SEPARATOR = 0x800;
    public const uint BYPOSITION = 0x400;
    public const uint BYCOMMAND = 0x0;
    public const uint GRAYED = 0x1;
    public const uint POPUP = 0x10;
}

public static class WM
{
    public const int INITMENUPOPUP = 0x0117;
    public const int DRAWITEM = 0x002B;
    public const int MEASUREITEM = 0x002C;
    public const int MENUCHAR = 0x0120;
    public const int MENUSELECT = 0x011F;
    public const int DEVICECHANGE = 0x0219;
    public const int COPYDATA = 0x004A;
    public const int USER = 0x0400;
    public const int APP = 0x8000;
}

public static class SHIL
{
    public const int LARGE = 0;
    public const int SMALL = 1;
    public const int EXTRALARGE = 2;
    public const int SYSSMALL = 3;
    public const int JUMBO = 4;
}

public static class SEE
{
    public const uint MASK_IDLIST = 0x4;
    public const uint MASK_INVOKEIDLIST = 0xC;
    public const uint MASK_NOCLOSEPROCESS = 0x40;
    public const uint MASK_FLAG_NO_UI = 0x400;
    public const uint MASK_UNICODE = 0x4000;
    public const uint MASK_ASYNCOK = 0x100000;
}

public static class FileAttr
{
    public const uint READONLY = 0x1;
    public const uint HIDDEN = 0x2;
    public const uint SYSTEM = 0x4;
    public const uint DIRECTORY = 0x10;
    public const uint ARCHIVE = 0x20;
    public const uint NORMAL = 0x80;
    public const uint TEMPORARY = 0x100;
    public const uint REPARSE_POINT = 0x400;
    public const uint COMPRESSED = 0x800;
    public const uint OFFLINE = 0x1000;
    public const uint ENCRYPTED = 0x4000;
    public const uint RECALL_ON_OPEN = 0x40000;
    public const uint RECALL_ON_DATA_ACCESS = 0x400000;
}

public static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int ERROR_CANCELLED = unchecked((int)0x800704C7);
    public const int COPYENGINE_E_USER_CANCELLED = unchecked((int)0x80270000);
}

public static class IIDs
{
    public static readonly Guid IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    public static readonly Guid IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IContextMenu = new("000214e4-0000-0000-c000-000000000046");
    public static readonly Guid IDataObject = new("0000010e-0000-0000-C000-000000000046");
    public static readonly Guid IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    public static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
}

public static class PropertyKeys
{
    private static readonly Guid Storage = new("b725f130-47ef-101a-a5f1-02608c9eebac");
    public static PROPERTYKEY Size => new(Storage, 12);
    public static PROPERTYKEY DateModified => new(Storage, 14);
    public static PROPERTYKEY ItemTypeText => new(Storage, 4);
    // Volume capacity/free space (PKEY_Capacity, PKEY_FreeSpace)
    public static PROPERTYKEY Capacity => new(new Guid("9b174b35-40ff-11d2-a27e-00c04fc30871"), 3);
    public static PROPERTYKEY FreeSpace => new(new Guid("9b174b35-40ff-11d2-a27e-00c04fc30871"), 2);
}

namespace Egami.SystemDrawing;

/// <summary>Where content sits in a rectangle.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public enum ContentAlignment
{
    /// <summary>Top Left.</summary>
    TopLeft = 1,

    /// <summary>Top Center.</summary>
    TopCenter = 2,

    /// <summary>Top Right.</summary>
    TopRight = 4,

    /// <summary>Middle Left.</summary>
    MiddleLeft = 16,

    /// <summary>Middle Center.</summary>
    MiddleCenter = 32,

    /// <summary>Middle Right.</summary>
    MiddleRight = 64,

    /// <summary>Bottom Left.</summary>
    BottomLeft = 256,

    /// <summary>Bottom Center.</summary>
    BottomCenter = 512,

    /// <summary>Bottom Right.</summary>
    BottomRight = 1024,
}

/// <summary>Windows' stock shell icons, which only Windows has; <see cref="SystemIcons.GetStockIcon(StockIconId, int)"/> throws.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public enum StockIconId
{
    /// <summary>Document No Association.</summary>
    DocumentNoAssociation = 0,

    /// <summary>Document With Association.</summary>
    DocumentWithAssociation = 1,

    /// <summary>Application.</summary>
    Application = 2,

    /// <summary>Folder.</summary>
    Folder = 3,

    /// <summary>Folder Open.</summary>
    FolderOpen = 4,

    /// <summary>Drive525.</summary>
    Drive525 = 5,

    /// <summary>Drive35.</summary>
    Drive35 = 6,

    /// <summary>Drive Removable.</summary>
    DriveRemovable = 7,

    /// <summary>Drive Fixed.</summary>
    DriveFixed = 8,

    /// <summary>Drive Net.</summary>
    DriveNet = 9,

    /// <summary>Drive Net Disabled.</summary>
    DriveNetDisabled = 10,

    /// <summary>Drive CD.</summary>
    DriveCD = 11,

    /// <summary>Drive Ram.</summary>
    DriveRam = 12,

    /// <summary>World.</summary>
    World = 13,

    /// <summary>Server.</summary>
    Server = 15,

    /// <summary>Printer.</summary>
    Printer = 16,

    /// <summary>My Network.</summary>
    MyNetwork = 17,

    /// <summary>Find.</summary>
    Find = 22,

    /// <summary>Help.</summary>
    Help = 23,

    /// <summary>Share.</summary>
    Share = 28,

    /// <summary>Link.</summary>
    Link = 29,

    /// <summary>Slow File.</summary>
    SlowFile = 30,

    /// <summary>Recycler.</summary>
    Recycler = 31,

    /// <summary>Recycler Full.</summary>
    RecyclerFull = 32,

    /// <summary>Media CDAudio.</summary>
    MediaCDAudio = 40,

    /// <summary>Lock.</summary>
    Lock = 47,

    /// <summary>Auto List.</summary>
    AutoList = 49,

    /// <summary>Printer Net.</summary>
    PrinterNet = 50,

    /// <summary>Server Share.</summary>
    ServerShare = 51,

    /// <summary>Printer Fax.</summary>
    PrinterFax = 52,

    /// <summary>Printer Fax Net.</summary>
    PrinterFaxNet = 53,

    /// <summary>Printer File.</summary>
    PrinterFile = 54,

    /// <summary>Stack.</summary>
    Stack = 55,

    /// <summary>Media SVCD.</summary>
    MediaSVCD = 56,

    /// <summary>Stuffed Folder.</summary>
    StuffedFolder = 57,

    /// <summary>Drive Unknown.</summary>
    DriveUnknown = 58,

    /// <summary>Drive DVD.</summary>
    DriveDVD = 59,

    /// <summary>Media DVD.</summary>
    MediaDVD = 60,

    /// <summary>Media DVDRAM.</summary>
    MediaDVDRAM = 61,

    /// <summary>Media DVDRW.</summary>
    MediaDVDRW = 62,

    /// <summary>Media DVDR.</summary>
    MediaDVDR = 63,

    /// <summary>Media DVDROM.</summary>
    MediaDVDROM = 64,

    /// <summary>Media CDAudio Plus.</summary>
    MediaCDAudioPlus = 65,

    /// <summary>Media CDRW.</summary>
    MediaCDRW = 66,

    /// <summary>Media CDR.</summary>
    MediaCDR = 67,

    /// <summary>Media CDBurn.</summary>
    MediaCDBurn = 68,

    /// <summary>Media Blank CD.</summary>
    MediaBlankCD = 69,

    /// <summary>Media CDROM.</summary>
    MediaCDROM = 70,

    /// <summary>Audio Files.</summary>
    AudioFiles = 71,

    /// <summary>Image Files.</summary>
    ImageFiles = 72,

    /// <summary>Video Files.</summary>
    VideoFiles = 73,

    /// <summary>Mixed Files.</summary>
    MixedFiles = 74,

    /// <summary>Folder Back.</summary>
    FolderBack = 75,

    /// <summary>Folder Front.</summary>
    FolderFront = 76,

    /// <summary>Shield.</summary>
    Shield = 77,

    /// <summary>Warning.</summary>
    Warning = 78,

    /// <summary>Info.</summary>
    Info = 79,

    /// <summary>Error.</summary>
    Error = 80,

    /// <summary>Key.</summary>
    Key = 81,

    /// <summary>Software.</summary>
    Software = 82,

    /// <summary>Rename.</summary>
    Rename = 83,

    /// <summary>Delete.</summary>
    Delete = 84,

    /// <summary>Media Audio DVD.</summary>
    MediaAudioDVD = 85,

    /// <summary>Media Movie DVD.</summary>
    MediaMovieDVD = 86,

    /// <summary>Media Enhanced CD.</summary>
    MediaEnhancedCD = 87,

    /// <summary>Media Enhanced DVD.</summary>
    MediaEnhancedDVD = 88,

    /// <summary>Media HDDVD.</summary>
    MediaHDDVD = 89,

    /// <summary>Media Blu Ray.</summary>
    MediaBluRay = 90,

    /// <summary>Media VCD.</summary>
    MediaVCD = 91,

    /// <summary>Media DVDPlus R.</summary>
    MediaDVDPlusR = 92,

    /// <summary>Media DVDPlus RW.</summary>
    MediaDVDPlusRW = 93,

    /// <summary>Desktop PC.</summary>
    DesktopPC = 94,

    /// <summary>Mobile PC.</summary>
    MobilePC = 95,

    /// <summary>Users.</summary>
    Users = 96,

    /// <summary>Media Smart Media.</summary>
    MediaSmartMedia = 97,

    /// <summary>Media Compact Flash.</summary>
    MediaCompactFlash = 98,

    /// <summary>Device Cell Phone.</summary>
    DeviceCellPhone = 99,

    /// <summary>Device Camera.</summary>
    DeviceCamera = 100,

    /// <summary>Device Video Camera.</summary>
    DeviceVideoCamera = 101,

    /// <summary>Device Audio Player.</summary>
    DeviceAudioPlayer = 102,

    /// <summary>Network Connect.</summary>
    NetworkConnect = 103,

    /// <summary>Internet.</summary>
    Internet = 104,

    /// <summary>Zip File.</summary>
    ZipFile = 105,

    /// <summary>Settings.</summary>
    Settings = 106,

    /// <summary>Drive HDDVD.</summary>
    DriveHDDVD = 132,

    /// <summary>Drive BD.</summary>
    DriveBD = 133,

    /// <summary>Media HDDVDROM.</summary>
    MediaHDDVDROM = 134,

    /// <summary>Media HDDVDR.</summary>
    MediaHDDVDR = 135,

    /// <summary>Media HDDVDRAM.</summary>
    MediaHDDVDRAM = 136,

    /// <summary>Media BDROM.</summary>
    MediaBDROM = 137,

    /// <summary>Media BDR.</summary>
    MediaBDR = 138,

    /// <summary>Media BDRE.</summary>
    MediaBDRE = 139,

    /// <summary>Clustered Drive.</summary>
    ClusteredDrive = 140,
}

/// <summary>How Windows would draw a stock icon.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
[Flags]
public enum StockIconOptions
{
    /// <summary>Default.</summary>
    Default = 0,

    /// <summary>Small Icon.</summary>
    SmallIcon = 1,

    /// <summary>Shell Icon Size.</summary>
    ShellIconSize = 4,

    /// <summary>Link Overlay.</summary>
    LinkOverlay = 32768,

    /// <summary>Selected.</summary>
    Selected = 65536,
}

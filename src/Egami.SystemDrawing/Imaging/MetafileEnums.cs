namespace Egami.SystemDrawing.Imaging;

/// <summary>The type of a metafile record, as <see cref="Graphics.EnumerateMetafile(Metafile, PointF, Graphics.EnumerateMetafileProc)"/> reports it: WMF functions above 0x10000, EMF records from 1, EMF+ records from 0x4000.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public enum EmfPlusRecordType
{
    /// <summary>Added to a WMF function number to make its record type.</summary>
    WmfRecordBase = 65536,

    /// <summary>The WMF Set Bk Color record (function 0x201).</summary>
    WmfSetBkColor = 66049,

    /// <summary>The WMF Set Bk Mode record (function 0x102).</summary>
    WmfSetBkMode = 65794,

    /// <summary>The WMF Set Map Mode record (function 0x103).</summary>
    WmfSetMapMode = 65795,

    /// <summary>The WMF Set ROP2 record (function 0x104).</summary>
    WmfSetROP2 = 65796,

    /// <summary>The WMF Set Rel Abs record (function 0x105).</summary>
    WmfSetRelAbs = 65797,

    /// <summary>The WMF Set Poly Fill Mode record (function 0x106).</summary>
    WmfSetPolyFillMode = 65798,

    /// <summary>The WMF Set Stretch Blt Mode record (function 0x107).</summary>
    WmfSetStretchBltMode = 65799,

    /// <summary>The WMF Set Text Char Extra record (function 0x108).</summary>
    WmfSetTextCharExtra = 65800,

    /// <summary>The WMF Set Text Color record (function 0x209).</summary>
    WmfSetTextColor = 66057,

    /// <summary>The WMF Set Text Justification record (function 0x20A).</summary>
    WmfSetTextJustification = 66058,

    /// <summary>The WMF Set Window Org record (function 0x20B).</summary>
    WmfSetWindowOrg = 66059,

    /// <summary>The WMF Set Window Ext record (function 0x20C).</summary>
    WmfSetWindowExt = 66060,

    /// <summary>The WMF Set Viewport Org record (function 0x20D).</summary>
    WmfSetViewportOrg = 66061,

    /// <summary>The WMF Set Viewport Ext record (function 0x20E).</summary>
    WmfSetViewportExt = 66062,

    /// <summary>The WMF Offset Window Org record (function 0x20F).</summary>
    WmfOffsetWindowOrg = 66063,

    /// <summary>The WMF Scale Window Ext record (function 0x410).</summary>
    WmfScaleWindowExt = 66576,

    /// <summary>The WMF Offset Viewport Org record (function 0x211).</summary>
    WmfOffsetViewportOrg = 66065,

    /// <summary>The WMF Scale Viewport Ext record (function 0x412).</summary>
    WmfScaleViewportExt = 66578,

    /// <summary>The WMF Line To record (function 0x213).</summary>
    WmfLineTo = 66067,

    /// <summary>The WMF Move To record (function 0x214).</summary>
    WmfMoveTo = 66068,

    /// <summary>The WMF Exclude Clip Rect record (function 0x415).</summary>
    WmfExcludeClipRect = 66581,

    /// <summary>The WMF Intersect Clip Rect record (function 0x416).</summary>
    WmfIntersectClipRect = 66582,

    /// <summary>The WMF Arc record (function 0x817).</summary>
    WmfArc = 67607,

    /// <summary>The WMF Ellipse record (function 0x418).</summary>
    WmfEllipse = 66584,

    /// <summary>The WMF Flood Fill record (function 0x419).</summary>
    WmfFloodFill = 66585,

    /// <summary>The WMF Pie record (function 0x81A).</summary>
    WmfPie = 67610,

    /// <summary>The WMF Rectangle record (function 0x41B).</summary>
    WmfRectangle = 66587,

    /// <summary>The WMF Round Rect record (function 0x61C).</summary>
    WmfRoundRect = 67100,

    /// <summary>The WMF Pat Blt record (function 0x61D).</summary>
    WmfPatBlt = 67101,

    /// <summary>The WMF Save DC record (function 0x1E).</summary>
    WmfSaveDC = 65566,

    /// <summary>The WMF Set Pixel record (function 0x41F).</summary>
    WmfSetPixel = 66591,

    /// <summary>The WMF Offset Cilp Rgn record (function 0x220).</summary>
    WmfOffsetCilpRgn = 66080,

    /// <summary>The WMF Text Out record (function 0x521).</summary>
    WmfTextOut = 66849,

    /// <summary>The WMF Bit Blt record (function 0x922).</summary>
    WmfBitBlt = 67874,

    /// <summary>The WMF Stretch Blt record (function 0xB23).</summary>
    WmfStretchBlt = 68387,

    /// <summary>The WMF Polygon record (function 0x324).</summary>
    WmfPolygon = 66340,

    /// <summary>The WMF Polyline record (function 0x325).</summary>
    WmfPolyline = 66341,

    /// <summary>The WMF Escape record (function 0x626).</summary>
    WmfEscape = 67110,

    /// <summary>The WMF Restore DC record (function 0x127).</summary>
    WmfRestoreDC = 65831,

    /// <summary>The WMF Fill Region record (function 0x228).</summary>
    WmfFillRegion = 66088,

    /// <summary>The WMF Frame Region record (function 0x429).</summary>
    WmfFrameRegion = 66601,

    /// <summary>The WMF Invert Region record (function 0x12A).</summary>
    WmfInvertRegion = 65834,

    /// <summary>The WMF Paint Region record (function 0x12B).</summary>
    WmfPaintRegion = 65835,

    /// <summary>The WMF Select Clip Region record (function 0x12C).</summary>
    WmfSelectClipRegion = 65836,

    /// <summary>The WMF Select Object record (function 0x12D).</summary>
    WmfSelectObject = 65837,

    /// <summary>The WMF Set Text Align record (function 0x12E).</summary>
    WmfSetTextAlign = 65838,

    /// <summary>The WMF Chord record (function 0x830).</summary>
    WmfChord = 67632,

    /// <summary>The WMF Set Mapper Flags record (function 0x231).</summary>
    WmfSetMapperFlags = 66097,

    /// <summary>The WMF Ext Text Out record (function 0xA32).</summary>
    WmfExtTextOut = 68146,

    /// <summary>The WMF Set Dib To Dev record (function 0xD33).</summary>
    WmfSetDibToDev = 68915,

    /// <summary>The WMF Select Palette record (function 0x234).</summary>
    WmfSelectPalette = 66100,

    /// <summary>The WMF Realize Palette record (function 0x35).</summary>
    WmfRealizePalette = 65589,

    /// <summary>The WMF Animate Palette record (function 0x436).</summary>
    WmfAnimatePalette = 66614,

    /// <summary>The WMF Set Pal Entries record (function 0x37).</summary>
    WmfSetPalEntries = 65591,

    /// <summary>The WMF Poly Polygon record (function 0x538).</summary>
    WmfPolyPolygon = 66872,

    /// <summary>The WMF Resize Palette record (function 0x139).</summary>
    WmfResizePalette = 65849,

    /// <summary>The WMF Dib Bit Blt record (function 0x940).</summary>
    WmfDibBitBlt = 67904,

    /// <summary>The WMF Dib Stretch Blt record (function 0xB41).</summary>
    WmfDibStretchBlt = 68417,

    /// <summary>The WMF Dib Create Pattern Brush record (function 0x142).</summary>
    WmfDibCreatePatternBrush = 65858,

    /// <summary>The WMF Stretch Dib record (function 0xF43).</summary>
    WmfStretchDib = 69443,

    /// <summary>The WMF Ext Flood Fill record (function 0x548).</summary>
    WmfExtFloodFill = 66888,

    /// <summary>The WMF Set Layout record (function 0x149).</summary>
    WmfSetLayout = 65865,

    /// <summary>The WMF Delete Object record (function 0x1F0).</summary>
    WmfDeleteObject = 66032,

    /// <summary>The WMF Create Palette record (function 0xF7).</summary>
    WmfCreatePalette = 65783,

    /// <summary>The WMF Create Pattern Brush record (function 0x1F9).</summary>
    WmfCreatePatternBrush = 66041,

    /// <summary>The WMF Create Pen Indirect record (function 0x2FA).</summary>
    WmfCreatePenIndirect = 66298,

    /// <summary>The WMF Create Font Indirect record (function 0x2FB).</summary>
    WmfCreateFontIndirect = 66299,

    /// <summary>The WMF Create Brush Indirect record (function 0x2FC).</summary>
    WmfCreateBrushIndirect = 66300,

    /// <summary>The WMF Create Region record (function 0x6FF).</summary>
    WmfCreateRegion = 67327,

    /// <summary>The EMF Header record.</summary>
    EmfHeader = 1,

    /// <summary>The EMF Poly Bezier record.</summary>
    EmfPolyBezier = 2,

    /// <summary>The EMF Polygon record.</summary>
    EmfPolygon = 3,

    /// <summary>The EMF Polyline record.</summary>
    EmfPolyline = 4,

    /// <summary>The EMF Poly Bezier To record.</summary>
    EmfPolyBezierTo = 5,

    /// <summary>The EMF Poly Line To record.</summary>
    EmfPolyLineTo = 6,

    /// <summary>The EMF Poly Polyline record.</summary>
    EmfPolyPolyline = 7,

    /// <summary>The EMF Poly Polygon record.</summary>
    EmfPolyPolygon = 8,

    /// <summary>The EMF Set Window Ext Ex record.</summary>
    EmfSetWindowExtEx = 9,

    /// <summary>The EMF Set Window Org Ex record.</summary>
    EmfSetWindowOrgEx = 10,

    /// <summary>The EMF Set Viewport Ext Ex record.</summary>
    EmfSetViewportExtEx = 11,

    /// <summary>The EMF Set Viewport Org Ex record.</summary>
    EmfSetViewportOrgEx = 12,

    /// <summary>The EMF Set Brush Org Ex record.</summary>
    EmfSetBrushOrgEx = 13,

    /// <summary>The EMF Eof record.</summary>
    EmfEof = 14,

    /// <summary>The EMF Set Pixel V record.</summary>
    EmfSetPixelV = 15,

    /// <summary>The EMF Set Mapper Flags record.</summary>
    EmfSetMapperFlags = 16,

    /// <summary>The EMF Set Map Mode record.</summary>
    EmfSetMapMode = 17,

    /// <summary>The EMF Set Bk Mode record.</summary>
    EmfSetBkMode = 18,

    /// <summary>The EMF Set Poly Fill Mode record.</summary>
    EmfSetPolyFillMode = 19,

    /// <summary>The EMF Set ROP2 record.</summary>
    EmfSetROP2 = 20,

    /// <summary>The EMF Set Stretch Blt Mode record.</summary>
    EmfSetStretchBltMode = 21,

    /// <summary>The EMF Set Text Align record.</summary>
    EmfSetTextAlign = 22,

    /// <summary>The EMF Set Color Adjustment record.</summary>
    EmfSetColorAdjustment = 23,

    /// <summary>The EMF Set Text Color record.</summary>
    EmfSetTextColor = 24,

    /// <summary>The EMF Set Bk Color record.</summary>
    EmfSetBkColor = 25,

    /// <summary>The EMF Offset Clip Rgn record.</summary>
    EmfOffsetClipRgn = 26,

    /// <summary>The EMF Move To Ex record.</summary>
    EmfMoveToEx = 27,

    /// <summary>The EMF Set Meta Rgn record.</summary>
    EmfSetMetaRgn = 28,

    /// <summary>The EMF Exclude Clip Rect record.</summary>
    EmfExcludeClipRect = 29,

    /// <summary>The EMF Intersect Clip Rect record.</summary>
    EmfIntersectClipRect = 30,

    /// <summary>The EMF Scale Viewport Ext Ex record.</summary>
    EmfScaleViewportExtEx = 31,

    /// <summary>The EMF Scale Window Ext Ex record.</summary>
    EmfScaleWindowExtEx = 32,

    /// <summary>The EMF Save DC record.</summary>
    EmfSaveDC = 33,

    /// <summary>The EMF Restore DC record.</summary>
    EmfRestoreDC = 34,

    /// <summary>The EMF Set World Transform record.</summary>
    EmfSetWorldTransform = 35,

    /// <summary>The EMF Modify World Transform record.</summary>
    EmfModifyWorldTransform = 36,

    /// <summary>The EMF Select Object record.</summary>
    EmfSelectObject = 37,

    /// <summary>The EMF Create Pen record.</summary>
    EmfCreatePen = 38,

    /// <summary>The EMF Create Brush Indirect record.</summary>
    EmfCreateBrushIndirect = 39,

    /// <summary>The EMF Delete Object record.</summary>
    EmfDeleteObject = 40,

    /// <summary>The EMF Angle Arc record.</summary>
    EmfAngleArc = 41,

    /// <summary>The EMF Ellipse record.</summary>
    EmfEllipse = 42,

    /// <summary>The EMF Rectangle record.</summary>
    EmfRectangle = 43,

    /// <summary>The EMF Round Rect record.</summary>
    EmfRoundRect = 44,

    /// <summary>The EMF Round Arc record.</summary>
    EmfRoundArc = 45,

    /// <summary>The EMF Chord record.</summary>
    EmfChord = 46,

    /// <summary>The EMF Pie record.</summary>
    EmfPie = 47,

    /// <summary>The EMF Select Palette record.</summary>
    EmfSelectPalette = 48,

    /// <summary>The EMF Create Palette record.</summary>
    EmfCreatePalette = 49,

    /// <summary>The EMF Set Palette Entries record.</summary>
    EmfSetPaletteEntries = 50,

    /// <summary>The EMF Resize Palette record.</summary>
    EmfResizePalette = 51,

    /// <summary>The EMF Realize Palette record.</summary>
    EmfRealizePalette = 52,

    /// <summary>The EMF Ext Flood Fill record.</summary>
    EmfExtFloodFill = 53,

    /// <summary>The EMF Line To record.</summary>
    EmfLineTo = 54,

    /// <summary>The EMF Arc To record.</summary>
    EmfArcTo = 55,

    /// <summary>The EMF Poly Draw record.</summary>
    EmfPolyDraw = 56,

    /// <summary>The EMF Set Arc Direction record.</summary>
    EmfSetArcDirection = 57,

    /// <summary>The EMF Set Miter Limit record.</summary>
    EmfSetMiterLimit = 58,

    /// <summary>The EMF Begin Path record.</summary>
    EmfBeginPath = 59,

    /// <summary>The EMF End Path record.</summary>
    EmfEndPath = 60,

    /// <summary>The EMF Close Figure record.</summary>
    EmfCloseFigure = 61,

    /// <summary>The EMF Fill Path record.</summary>
    EmfFillPath = 62,

    /// <summary>The EMF Stroke And Fill Path record.</summary>
    EmfStrokeAndFillPath = 63,

    /// <summary>The EMF Stroke Path record.</summary>
    EmfStrokePath = 64,

    /// <summary>The EMF Flatten Path record.</summary>
    EmfFlattenPath = 65,

    /// <summary>The EMF Widen Path record.</summary>
    EmfWidenPath = 66,

    /// <summary>The EMF Select Clip Path record.</summary>
    EmfSelectClipPath = 67,

    /// <summary>The EMF Abort Path record.</summary>
    EmfAbortPath = 68,

    /// <summary>The EMF Reserved069 record.</summary>
    EmfReserved069 = 69,

    /// <summary>The EMF Gdi Comment record.</summary>
    EmfGdiComment = 70,

    /// <summary>The EMF Fill Rgn record.</summary>
    EmfFillRgn = 71,

    /// <summary>The EMF Frame Rgn record.</summary>
    EmfFrameRgn = 72,

    /// <summary>The EMF Invert Rgn record.</summary>
    EmfInvertRgn = 73,

    /// <summary>The EMF Paint Rgn record.</summary>
    EmfPaintRgn = 74,

    /// <summary>The EMF Ext Select Clip Rgn record.</summary>
    EmfExtSelectClipRgn = 75,

    /// <summary>The EMF Bit Blt record.</summary>
    EmfBitBlt = 76,

    /// <summary>The EMF Stretch Blt record.</summary>
    EmfStretchBlt = 77,

    /// <summary>The EMF Mask Blt record.</summary>
    EmfMaskBlt = 78,

    /// <summary>The EMF Plg Blt record.</summary>
    EmfPlgBlt = 79,

    /// <summary>The EMF Set DIBits To Device record.</summary>
    EmfSetDIBitsToDevice = 80,

    /// <summary>The EMF Stretch DIBits record.</summary>
    EmfStretchDIBits = 81,

    /// <summary>The EMF Ext Create Font Indirect record.</summary>
    EmfExtCreateFontIndirect = 82,

    /// <summary>The EMF Ext Text Out A record.</summary>
    EmfExtTextOutA = 83,

    /// <summary>The EMF Ext Text Out W record.</summary>
    EmfExtTextOutW = 84,

    /// <summary>The EMF Poly Bezier16 record.</summary>
    EmfPolyBezier16 = 85,

    /// <summary>The EMF Polygon16 record.</summary>
    EmfPolygon16 = 86,

    /// <summary>The EMF Polyline16 record.</summary>
    EmfPolyline16 = 87,

    /// <summary>The EMF Poly Bezier To16 record.</summary>
    EmfPolyBezierTo16 = 88,

    /// <summary>The EMF Polyline To16 record.</summary>
    EmfPolylineTo16 = 89,

    /// <summary>The EMF Poly Polyline16 record.</summary>
    EmfPolyPolyline16 = 90,

    /// <summary>The EMF Poly Polygon16 record.</summary>
    EmfPolyPolygon16 = 91,

    /// <summary>The EMF Poly Draw16 record.</summary>
    EmfPolyDraw16 = 92,

    /// <summary>The EMF Create Mono Brush record.</summary>
    EmfCreateMonoBrush = 93,

    /// <summary>The EMF Create Dib Pattern Brush Pt record.</summary>
    EmfCreateDibPatternBrushPt = 94,

    /// <summary>The EMF Ext Create Pen record.</summary>
    EmfExtCreatePen = 95,

    /// <summary>The EMF Poly Text Out A record.</summary>
    EmfPolyTextOutA = 96,

    /// <summary>The EMF Poly Text Out W record.</summary>
    EmfPolyTextOutW = 97,

    /// <summary>The EMF Set Icm Mode record.</summary>
    EmfSetIcmMode = 98,

    /// <summary>The EMF Create Color Space record.</summary>
    EmfCreateColorSpace = 99,

    /// <summary>The EMF Set Color Space record.</summary>
    EmfSetColorSpace = 100,

    /// <summary>The EMF Delete Color Space record.</summary>
    EmfDeleteColorSpace = 101,

    /// <summary>The EMF Gls Record record.</summary>
    EmfGlsRecord = 102,

    /// <summary>The EMF Gls Bounded Record record.</summary>
    EmfGlsBoundedRecord = 103,

    /// <summary>The EMF Pixel Format record.</summary>
    EmfPixelFormat = 104,

    /// <summary>The EMF Draw Escape record.</summary>
    EmfDrawEscape = 105,

    /// <summary>The EMF Ext Escape record.</summary>
    EmfExtEscape = 106,

    /// <summary>The EMF Start Doc record.</summary>
    EmfStartDoc = 107,

    /// <summary>The EMF Small Text Out record.</summary>
    EmfSmallTextOut = 108,

    /// <summary>The EMF Force Ufi Mapping record.</summary>
    EmfForceUfiMapping = 109,

    /// <summary>The EMF Named Escpae record.</summary>
    EmfNamedEscpae = 110,

    /// <summary>The EMF Color Correct Palette record.</summary>
    EmfColorCorrectPalette = 111,

    /// <summary>The EMF Set Icm Profile A record.</summary>
    EmfSetIcmProfileA = 112,

    /// <summary>The EMF Set Icm Profile W record.</summary>
    EmfSetIcmProfileW = 113,

    /// <summary>The EMF Alpha Blend record.</summary>
    EmfAlphaBlend = 114,

    /// <summary>The EMF Set Layout record.</summary>
    EmfSetLayout = 115,

    /// <summary>The EMF Transparent Blt record.</summary>
    EmfTransparentBlt = 116,

    /// <summary>The EMF Reserved117 record.</summary>
    EmfReserved117 = 117,

    /// <summary>The EMF Gradient Fill record.</summary>
    EmfGradientFill = 118,

    /// <summary>The EMF Set Linked Ufis record.</summary>
    EmfSetLinkedUfis = 119,

    /// <summary>The EMF Set Text Justification record.</summary>
    EmfSetTextJustification = 120,

    /// <summary>The EMF Color Match To Target W record.</summary>
    EmfColorMatchToTargetW = 121,

    /// <summary>The EMF Create Color Space W record.</summary>
    EmfCreateColorSpaceW = 122,

    /// <summary>The EMF Max record.</summary>
    EmfMax = 122,

    /// <summary>The EMF Min record.</summary>
    EmfMin = 1,

    /// <summary>The EMF+ Record Base record.</summary>
    EmfPlusRecordBase = 16384,

    /// <summary>The EMF+ Invalid record.</summary>
    Invalid = 16384,

    /// <summary>The EMF+ Header record.</summary>
    Header = 16385,

    /// <summary>The EMF+ End Of File record.</summary>
    EndOfFile = 16386,

    /// <summary>The EMF+ Comment record.</summary>
    Comment = 16387,

    /// <summary>The EMF+ Get DC record.</summary>
    GetDC = 16388,

    /// <summary>The EMF+ Multi Format Start record.</summary>
    MultiFormatStart = 16389,

    /// <summary>The EMF+ Multi Format Section record.</summary>
    MultiFormatSection = 16390,

    /// <summary>The EMF+ Multi Format End record.</summary>
    MultiFormatEnd = 16391,

    /// <summary>The EMF+ Object record.</summary>
    Object = 16392,

    /// <summary>The EMF+ Clear record.</summary>
    Clear = 16393,

    /// <summary>The EMF+ Fill Rects record.</summary>
    FillRects = 16394,

    /// <summary>The EMF+ Draw Rects record.</summary>
    DrawRects = 16395,

    /// <summary>The EMF+ Fill Polygon record.</summary>
    FillPolygon = 16396,

    /// <summary>The EMF+ Draw Lines record.</summary>
    DrawLines = 16397,

    /// <summary>The EMF+ Fill Ellipse record.</summary>
    FillEllipse = 16398,

    /// <summary>The EMF+ Draw Ellipse record.</summary>
    DrawEllipse = 16399,

    /// <summary>The EMF+ Fill Pie record.</summary>
    FillPie = 16400,

    /// <summary>The EMF+ Draw Pie record.</summary>
    DrawPie = 16401,

    /// <summary>The EMF+ Draw Arc record.</summary>
    DrawArc = 16402,

    /// <summary>The EMF+ Fill Region record.</summary>
    FillRegion = 16403,

    /// <summary>The EMF+ Fill Path record.</summary>
    FillPath = 16404,

    /// <summary>The EMF+ Draw Path record.</summary>
    DrawPath = 16405,

    /// <summary>The EMF+ Fill Closed Curve record.</summary>
    FillClosedCurve = 16406,

    /// <summary>The EMF+ Draw Closed Curve record.</summary>
    DrawClosedCurve = 16407,

    /// <summary>The EMF+ Draw Curve record.</summary>
    DrawCurve = 16408,

    /// <summary>The EMF+ Draw Beziers record.</summary>
    DrawBeziers = 16409,

    /// <summary>The EMF+ Draw Image record.</summary>
    DrawImage = 16410,

    /// <summary>The EMF+ Draw Image Points record.</summary>
    DrawImagePoints = 16411,

    /// <summary>The EMF+ Draw String record.</summary>
    DrawString = 16412,

    /// <summary>The EMF+ Set Rendering Origin record.</summary>
    SetRenderingOrigin = 16413,

    /// <summary>The EMF+ Set Anti Alias Mode record.</summary>
    SetAntiAliasMode = 16414,

    /// <summary>The EMF+ Set Text Rendering Hint record.</summary>
    SetTextRenderingHint = 16415,

    /// <summary>The EMF+ Set Text Contrast record.</summary>
    SetTextContrast = 16416,

    /// <summary>The EMF+ Set Interpolation Mode record.</summary>
    SetInterpolationMode = 16417,

    /// <summary>The EMF+ Set Pixel Offset Mode record.</summary>
    SetPixelOffsetMode = 16418,

    /// <summary>The EMF+ Set Compositing Mode record.</summary>
    SetCompositingMode = 16419,

    /// <summary>The EMF+ Set Compositing Quality record.</summary>
    SetCompositingQuality = 16420,

    /// <summary>The EMF+ Save record.</summary>
    Save = 16421,

    /// <summary>The EMF+ Restore record.</summary>
    Restore = 16422,

    /// <summary>The EMF+ Begin Container record.</summary>
    BeginContainer = 16423,

    /// <summary>The EMF+ Begin Container No Params record.</summary>
    BeginContainerNoParams = 16424,

    /// <summary>The EMF+ End Container record.</summary>
    EndContainer = 16425,

    /// <summary>The EMF+ Set World Transform record.</summary>
    SetWorldTransform = 16426,

    /// <summary>The EMF+ Reset World Transform record.</summary>
    ResetWorldTransform = 16427,

    /// <summary>The EMF+ Multiply World Transform record.</summary>
    MultiplyWorldTransform = 16428,

    /// <summary>The EMF+ Translate World Transform record.</summary>
    TranslateWorldTransform = 16429,

    /// <summary>The EMF+ Scale World Transform record.</summary>
    ScaleWorldTransform = 16430,

    /// <summary>The EMF+ Rotate World Transform record.</summary>
    RotateWorldTransform = 16431,

    /// <summary>The EMF+ Set Page Transform record.</summary>
    SetPageTransform = 16432,

    /// <summary>The EMF+ Reset Clip record.</summary>
    ResetClip = 16433,

    /// <summary>The EMF+ Set Clip Rect record.</summary>
    SetClipRect = 16434,

    /// <summary>The EMF+ Set Clip Path record.</summary>
    SetClipPath = 16435,

    /// <summary>The EMF+ Set Clip Region record.</summary>
    SetClipRegion = 16436,

    /// <summary>The EMF+ Offset Clip record.</summary>
    OffsetClip = 16437,

    /// <summary>The EMF+ Draw Driver String record.</summary>
    DrawDriverString = 16438,

    /// <summary>The EMF+ Total record.</summary>
    Total = 16439,

    /// <summary>The EMF+ Max record.</summary>
    Max = 16438,

    /// <summary>The EMF+ Min record.</summary>
    Min = 16385,
}

/// <summary>The kind of EMF a recording would write; Egami does not record metafiles.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public enum EmfType
{
    /// <summary>Emf Only.</summary>
    EmfOnly = 3,

    /// <summary>Emf Plus Only.</summary>
    EmfPlusOnly = 4,

    /// <summary>Emf Plus Dual.</summary>
    EmfPlusDual = 5,
}

/// <summary>What a metafile is, as its header reports it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public enum MetafileType
{
    /// <summary>Invalid.</summary>
    Invalid = 0,

    /// <summary>Wmf.</summary>
    Wmf = 1,

    /// <summary>Wmf Placeable.</summary>
    WmfPlaceable = 2,

    /// <summary>Emf.</summary>
    Emf = 3,

    /// <summary>Emf Plus Only.</summary>
    EmfPlusOnly = 4,

    /// <summary>Emf Plus Dual.</summary>
    EmfPlusDual = 5,
}

/// <summary>The unit of a recording's frame rectangle; Egami does not record metafiles.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public enum MetafileFrameUnit
{
    /// <summary>Pixel.</summary>
    Pixel = 2,

    /// <summary>Point.</summary>
    Point = 3,

    /// <summary>Inch.</summary>
    Inch = 4,

    /// <summary>Document.</summary>
    Document = 5,

    /// <summary>Millimeter.</summary>
    Millimeter = 6,

    /// <summary>Gdi Compatible.</summary>
    GdiCompatible = 7,
}

/// <summary>Whether a color key holds 32-bit or 64-bit colors.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum ColorMode
{
    /// <summary>Argb32 Mode.</summary>
    Argb32Mode = 0,

    /// <summary>Argb64 Mode.</summary>
    Argb64Mode = 1,
}

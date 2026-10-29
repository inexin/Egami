// Written by tools/sysdrawing-enumerate-metafile.py; each overload forwards to Graphics.Enumerate.
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing;

using Point = System.Drawing.Point;

/// <summary>The EnumerateMetafile overloads.</summary>
public sealed partial class Graphics
{
    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, unit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoint">The point the frame starts at.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, unit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, unit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destRect">The rectangle the frame fills.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destRect, srcRect, unit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, unit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, srcUnit));

    /// <summary>
    /// Walks the metafile's records, as GDI+ enumerates them, calling the callback for each until it returns false. Where
    /// the metafile would be drawn is not used: records are walked, not drawn.
    /// </summary>
    /// <param name="metafile">The metafile.</param>
    /// <param name="destPoints">Where the frame's corners go.</param>
    /// <param name="srcRect">The part of the frame.</param>
    /// <param name="unit">The unit of the part.</param>
    /// <param name="callback">Called for each record; returning false stops the walk.</param>
    /// <param name="callbackData">Not used.</param>
    /// <param name="imageAttr">Not used.</param>
    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes? imageAttr) => Enumerate(metafile, callback, () => DrawImage(metafile, destPoints, srcRect, unit));
}

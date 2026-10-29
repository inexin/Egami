"""Writes src/Egami.SystemDrawing/Graphics.Metafiles.cs: every EnumerateMetafile overload of Graphics, forwarding to one core.

Usage: python tools/sysdrawing-enumerate-metafile.py src/Egami.SystemDrawing/Graphics.Metafiles.cs
"""
import sys

DEST = [('PointF destPoint', 'The point the frame starts at.'), ('Point destPoint', 'The point the frame starts at.'),
        ('RectangleF destRect', 'The rectangle the frame fills.'), ('Rectangle destRect', 'The rectangle the frame fills.'),
        ('PointF[] destPoints', 'Where the frame\'s corners go.'), ('Point[] destPoints', 'Where the frame\'s corners go.')]
SRC = {'PointF': 'RectangleF', 'Point': 'Rectangle', 'RectangleF': 'RectangleF', 'Rectangle': 'Rectangle', 'PointF[]': 'RectangleF', 'Point[]': 'Rectangle'}
out = []
w = out.append


def doc(params):
    w('    /// <summary>')
    w('    /// Walks the metafile\'s records, as GDI+ enumerates them, calling the callback for each until it returns false. Where')
    w('    /// the metafile would be drawn is not used: records are walked, not drawn.')
    w('    /// </summary>')
    w('    /// <param name="metafile">The metafile.</param>')
    for name, text in params:
        w(f'    /// <param name="{name}">{text}</param>')


for dest, ddoc in DEST:
    dtype, dname = dest.split(' ')
    stype = SRC[dtype]
    for with_src in (False, True):
        for extra in ('', 'data', 'attr'):
            if extra == 'attr' and False:
                continue
            params = [(dname, ddoc)]
            sig = f'Metafile metafile, {dest}'
            if with_src:
                unit = 'unit' if extra == 'attr' else 'srcUnit'
                sig += f', {stype} srcRect, GraphicsUnit {unit}'
                params += [('srcRect', 'The part of the frame.'), (unit, 'The unit of the part.')]
            sig += ', EnumerateMetafileProc callback'
            params.append(('callback', 'Called for each record; returning false stops the walk.'))
            if extra in ('data', 'attr'):
                sig += ', IntPtr callbackData'
                params.append(('callbackData', 'Not used.'))
            if extra == 'attr':
                sig += ', ImageAttributes? imageAttr'
                params.append(('imageAttr', 'Not used.'))
            doc(params)
            if with_src:
                unit = 'unit' if extra == 'attr' else 'srcUnit'
                draw = {'PointF': f'DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, {unit})', 'Point': f'DrawImage(metafile, destPoint.X, destPoint.Y, srcRect, {unit})'}.get(dtype, f'DrawImage(metafile, {dname}, srcRect, {unit})')
            else:
                draw = f'DrawImage(metafile, {dname})'
            w(f'    public void EnumerateMetafile({sig}) => Enumerate(metafile, callback, () => {draw});')
            w('')

while out[-1] == '':
    out.pop()
header = '''// Written by tools/sysdrawing-enumerate-metafile.py; each overload forwards to Graphics.Enumerate.
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing;

using Point = System.Drawing.Point;

/// <summary>The EnumerateMetafile overloads.</summary>
public sealed partial class Graphics
{
'''
open(sys.argv[1], 'w', encoding='utf-8', newline='\n').write(header + '\n'.join(line.rstrip() for line in out) + '\n}\n')

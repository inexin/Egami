// The System.Drawing primitives (Color, Rectangle and the rest). Egami's own Size and Point would win inside Egami.* namespaces,
// so files that use those two alias them after their namespace line.
global using System.Drawing;

// The shared GDI+ code (src/Shared/GdiPlus) draws through Egami.Drawing, so Path means its geometry there; files here use
// System.IO.Path by its full name.
global using Path = Egami.Drawing.Path;

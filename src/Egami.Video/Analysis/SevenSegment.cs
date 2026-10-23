namespace Egami.Video.Analysis;

/// <summary>Draws digits and colons as seven-segment shapes, so the test pattern needs no font.</summary>
internal static class SevenSegment
{
    // Segments a to g as bits 0 to 6: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle.
    private static readonly int[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    /// <summary>Gets how wide <paramref name="text"/> is at a digit height of <paramref name="height"/>.</summary>
    public static int Width(string text, int height)
    {
        int width = 0;
        foreach (char c in text)
        {
            width += Advance(c, height);
        }

        return width;
    }

    /// <summary>Draws <paramref name="text"/> (digits and colons) in white with its top left at (x, y).</summary>
    public static void Draw(YuvPainter painter, string text, int x, int y, int height)
    {
        int digitWidth = Math.Max(3, height / 2);
        int thick = Math.Max(1, height / 10);
        int middle = y + ((height - thick) / 2);
        foreach (char c in text)
        {
            if (c is >= '0' and <= '9')
            {
                int bits = Digits[c - '0'];
                int right = x + digitWidth;
                void Segment(int bit, int x0, int y0, int x1, int y1)
                {
                    if ((bits & (1 << bit)) != 0)
                    {
                        painter.Fill(x0, y0, x1, y1, 1, 1, 1);
                    }
                }

                Segment(0, x, y, right, y + thick);
                Segment(1, right - thick, y, right, middle + thick);
                Segment(2, right - thick, middle, right, y + height);
                Segment(3, x, y + height - thick, right, y + height);
                Segment(4, x, middle, x + thick, y + height);
                Segment(5, x, y, x + thick, middle + thick);
                Segment(6, x, middle, right, middle + thick);
            }
            else if (c == ':')
            {
                painter.Fill(x, y + (height / 4), x + thick, y + (height / 4) + thick, 1, 1, 1);
                painter.Fill(x, y + (height * 3 / 4) - thick, x + thick, y + (height * 3 / 4), 1, 1, 1);
            }

            x += Advance(c, height);
        }
    }

    private static int Advance(char c, int height)
    {
        int thick = Math.Max(1, height / 10);
        return c == ':' ? thick * 3 : Math.Max(3, height / 2) + (thick * 2);
    }
}

namespace Egami.Formats.Extended;

/// <summary>A cursor over C source text, as XBM and XPM files are: it skips white space and comments and reads simple tokens.</summary>
internal ref struct CSource
{
    private readonly ReadOnlySpan<byte> _text;

    public CSource(ReadOnlySpan<byte> text)
    {
        _text = text;
    }

    /// <summary>Gets or sets the byte offset of the cursor.</summary>
    public int Position { get; set; }

    /// <summary>Gets whether the cursor is at the end of the text.</summary>
    public readonly bool AtEnd => Position >= _text.Length;

    /// <summary>Gets the byte at the cursor, or 0 at the end.</summary>
    public readonly byte Current => Position < _text.Length ? _text[Position] : (byte)0;

    /// <summary>Skips white space and /* */ and // comments.</summary>
    public void SkipSpace()
    {
        while (Position < _text.Length)
        {
            byte b = _text[Position];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v')
            {
                Position++;
            }
            else if (b == '/' && Position + 1 < _text.Length && _text[Position + 1] == '*')
            {
                int end = _text[(Position + 2)..].IndexOf("*/"u8);
                Position = end < 0 ? _text.Length : Position + 2 + end + 2;
            }
            else if (b == '/' && Position + 1 < _text.Length && _text[Position + 1] == '/')
            {
                int end = _text[Position..].IndexOf((byte)'\n');
                Position = end < 0 ? _text.Length : Position + end + 1;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Skips space, then returns whether <paramref name="token"/> follows, moving past it if so.</summary>
    public bool Accept(ReadOnlySpan<byte> token)
    {
        SkipSpace();
        if (_text[Position..].StartsWith(token))
        {
            Position += token.Length;
            return true;
        }

        return false;
    }

    /// <summary>Skips space, then reads a C identifier, or returns an empty span if none follows.</summary>
    public ReadOnlySpan<byte> Identifier()
    {
        SkipSpace();
        int start = Position;
        while (Position < _text.Length && (char.IsAsciiLetterOrDigit((char)_text[Position]) || _text[Position] == '_'))
        {
            Position++;
        }

        return _text[start..Position];
    }

    /// <summary>Skips space, then reads a decimal, octal or 0x hexadecimal integer; returns −1 if none follows or it is too large.</summary>
    public long Number()
    {
        SkipSpace();
        int radix = 10;
        if (Current == '0' && Position + 1 < _text.Length && (_text[Position + 1] | 0x20) == 'x')
        {
            radix = 16;
            Position += 2;
        }
        else if (Current == '0')
        {
            radix = 8;
        }

        long value = 0;
        int digits = 0;
        while (Position < _text.Length)
        {
            int digit = Digit(_text[Position]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            value = (value * radix) + digit;
            if (value > uint.MaxValue)
            {
                return -1;
            }

            digits++;
            Position++;
        }

        return digits > 0 || radix == 8 ? value : -1;
    }

    /// <summary>Moves past the next <paramref name="b"/>; returns <see langword="false"/> if there is none.</summary>
    public bool SkipPast(byte b)
    {
        int at = _text[Position..].IndexOf(b);
        if (at < 0)
        {
            Position = _text.Length;
            return false;
        }

        Position += at + 1;
        return true;
    }

    /// <summary>Returns the text from <paramref name="start"/> up to the cursor.</summary>
    public readonly ReadOnlySpan<byte> Since(int start) => _text[start..Position];

    private static int Digit(byte b)
    {
        return b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };
    }
}

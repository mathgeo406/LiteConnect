namespace LiteConnect.Terminal;

/// <summary>
/// Text styling flags applied to an individual <see cref="TerminalCell"/> via SGR sequences.
/// </summary>
[Flags]
public enum CellAttributes : byte
{
    None          = 0,
    Bold          = 1 << 0,
    Dim           = 1 << 1,
    Italic        = 1 << 2,
    Underline     = 1 << 3,
    Blink         = 1 << 4,
    Inverse       = 1 << 5,
    Hidden        = 1 << 6,
    Strikethrough = 1 << 7
}

/// <summary>
/// Discriminates how a <see cref="TerminalColor"/> is resolved at render time.
/// </summary>
public enum TerminalColorKind : byte
{
    /// <summary>Uses the active theme's default foreground or background color.</summary>
    Default = 0,

    /// <summary>Uses the 256-color xterm palette index stored in <see cref="TerminalColor.R"/> (0–255).</summary>
    Indexed = 1,

    /// <summary>Uses explicit 24-bit RGB values (<see cref="TerminalColor.R"/>, <see cref="TerminalColor.G"/>, <see cref="TerminalColor.B"/>).</summary>
    Rgb = 2
}

/// <summary>
/// Compact 4-byte color value supporting default theme colors, 16/256-color palette indices, and 24-bit TrueColor.
/// </summary>
public readonly record struct TerminalColor(TerminalColorKind Kind, byte R, byte G, byte B)
{
    public static TerminalColor Default => new(TerminalColorKind.Default, 0, 0, 0);

    public static TerminalColor FromIndex(byte index) => new(TerminalColorKind.Indexed, index, 0, 0);

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(TerminalColorKind.Rgb, r, g, b);

    public byte PaletteIndex => R;
}

/// <summary>
/// Represents a single character cell in the <see cref="TerminalBuffer"/>.
/// </summary>
public record struct TerminalCell(
    char Character,
    TerminalColor Foreground,
    TerminalColor Background,
    CellAttributes Attributes)
{
    public static TerminalCell Blank => new(' ', TerminalColor.Default, TerminalColor.Default, CellAttributes.None);

    public static TerminalCell CreateBlank(TerminalColor fg, TerminalColor bg, CellAttributes attrs = CellAttributes.None)
        => new(' ', fg, bg, attrs & ~(CellAttributes.Underline | CellAttributes.Strikethrough));
}

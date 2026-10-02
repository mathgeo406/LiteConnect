using System.Windows.Media;

namespace LiteConnect.Terminal;

/// <summary>
/// Defines a 16-color ANSI palette + default foreground, background, cursor, and selection colors,
/// and resolves any 256-color index or 24-bit TrueColor to a frozen <see cref="SolidColorBrush"/>.
/// </summary>
public sealed class TerminalTheme
{
    public string Name { get; }
    public Color DefaultForeground { get; }
    public Color DefaultBackground { get; }
    public Color CursorColor { get; }
    public Color SelectionColor { get; }

    public SolidColorBrush DefaultForegroundBrush { get; }
    public SolidColorBrush DefaultBackgroundBrush { get; }
    public SolidColorBrush CursorBrush { get; }
    public SolidColorBrush SelectionBrush { get; }

    private readonly Color[] _palette256 = new Color[256];
    private readonly SolidColorBrush[] _brushes256 = new SolidColorBrush[256];

    public static IReadOnlyList<string> AvailableSchemes { get; } =
    [
        "Default Dark",
        "Campbell",
        "Solarized Dark",
        "Dracula",
        "Monokai"
    ];

    public TerminalTheme(
        string name,
        Color defaultFg,
        Color defaultBg,
        Color cursor,
        Color selection,
        ReadOnlySpan<Color> ansi16)
    {
        Name              = name;
        DefaultForeground = defaultFg;
        DefaultBackground = defaultBg;
        CursorColor       = cursor;
        SelectionColor    = selection;

        DefaultForegroundBrush = CreateFrozenBrush(defaultFg);
        DefaultBackgroundBrush = CreateFrozenBrush(defaultBg);
        CursorBrush            = CreateFrozenBrush(cursor);
        SelectionBrush         = CreateFrozenBrush(selection);

        // 0–15: Theme ANSI 16 colors
        for (int i = 0; i < 16 && i < ansi16.Length; i++)
        {
            _palette256[i] = ansi16[i];
            _brushes256[i] = CreateFrozenBrush(ansi16[i]);
        }

        // 16–231: 6x6x6 xterm color cube
        ReadOnlySpan<byte> cubeLevels = [0, 95, 135, 175, 215, 255];
        int idx = 16;
        for (int r = 0; r < 6; r++)
        {
            for (int g = 0; g < 6; g++)
            {
                for (int b = 0; b < 6; b++)
                {
                    var c = Color.FromRgb(cubeLevels[r], cubeLevels[g], cubeLevels[b]);
                    _palette256[idx] = c;
                    _brushes256[idx] = CreateFrozenBrush(c);
                    idx++;
                }
            }
        }

        // 232–255: 24-step grayscale ramp
        for (int i = 0; i < 24; i++)
        {
            byte level = (byte)(8 + i * 10);
            var c = Color.FromRgb(level, level, level);
            _palette256[232 + i] = c;
            _brushes256[232 + i] = CreateFrozenBrush(c);
        }
    }

    public SolidColorBrush ResolveForegroundBrush(TerminalColor color, CellAttributes attrs)
    {
        if (attrs.HasFlag(CellAttributes.Hidden))
            return ResolveBackgroundBrush(color);

        if (color.Kind == TerminalColorKind.Default)
            return DefaultForegroundBrush;

        if (color.Kind == TerminalColorKind.Indexed)
        {
            byte idx = color.PaletteIndex;
            // Bold brightens standard 0–7 colors to 8–15
            if (attrs.HasFlag(CellAttributes.Bold) && idx < 8)
                idx = (byte)(idx + 8);
            return _brushes256[idx];
        }

        return CreateFrozenBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    public SolidColorBrush ResolveBackgroundBrush(TerminalColor color)
    {
        return color.Kind switch
        {
            TerminalColorKind.Default => DefaultBackgroundBrush,
            TerminalColorKind.Indexed => _brushes256[color.PaletteIndex],
            TerminalColorKind.Rgb     => CreateFrozenBrush(Color.FromRgb(color.R, color.G, color.B)),
            _                         => DefaultBackgroundBrush
        };
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static TerminalTheme FromName(string? schemeName) => (schemeName?.Trim()) switch
    {
        "Campbell"       => Campbell,
        "Solarized Dark" => SolarizedDark,
        "Dracula"        => Dracula,
        "Monokai"        => Monokai,
        _                => DefaultDark
    };

    public static TerminalTheme DefaultDark { get; } = new(
        "Default Dark",
        defaultFg: Color.FromRgb(0xD4, 0xD4, 0xD4),
        defaultBg: Color.FromRgb(0x1E, 0x1E, 0x1E),
        cursor:    Color.FromRgb(0x00, 0x7A, 0xCC),
        selection: Color.FromArgb(0x66, 0x26, 0x4F, 0x78),
        ansi16:
        [
            Color.FromRgb(0x1E, 0x1E, 0x1E), // 0 Black
            Color.FromRgb(0xCD, 0x31, 0x31), // 1 Red
            Color.FromRgb(0x0D, 0xBC, 0x79), // 2 Green
            Color.FromRgb(0xE5, 0xE5, 0x10), // 3 Yellow
            Color.FromRgb(0x24, 0x72, 0xC8), // 4 Blue
            Color.FromRgb(0xBC, 0x3F, 0xBC), // 5 Magenta
            Color.FromRgb(0x11, 0xA8, 0xCD), // 6 Cyan
            Color.FromRgb(0xE5, 0xE5, 0xE5), // 7 White
            Color.FromRgb(0x66, 0x66, 0x66), // 8 Bright Black
            Color.FromRgb(0xF1, 0x4C, 0x4C), // 9 Bright Red
            Color.FromRgb(0x23, 0xD1, 0x8B), // 10 Bright Green
            Color.FromRgb(0xF5, 0xF5, 0x43), // 11 Bright Yellow
            Color.FromRgb(0x3B, 0x8E, 0xEA), // 12 Bright Blue
            Color.FromRgb(0xD6, 0x70, 0xD6), // 13 Bright Magenta
            Color.FromRgb(0x29, 0xB8, 0xDB), // 14 Bright Cyan
            Color.FromRgb(0xFF, 0xFF, 0xFF)  // 15 Bright White
        ]);

    public static TerminalTheme Campbell { get; } = new(
        "Campbell",
        defaultFg: Color.FromRgb(0xCC, 0xCC, 0xCC),
        defaultBg: Color.FromRgb(0x0C, 0x0C, 0x0C),
        cursor:    Color.FromRgb(0xFF, 0xFF, 0xFF),
        selection: Color.FromArgb(0x66, 0x3A, 0x96, 0xDD),
        ansi16:
        [
            Color.FromRgb(0x0C, 0x0C, 0x0C),
            Color.FromRgb(0xC5, 0x0F, 0x1F),
            Color.FromRgb(0x13, 0xA1, 0x0E),
            Color.FromRgb(0xC1, 0x9C, 0x00),
            Color.FromRgb(0x00, 0x37, 0xDA),
            Color.FromRgb(0x88, 0x17, 0x98),
            Color.FromRgb(0x3A, 0x96, 0xDD),
            Color.FromRgb(0xCC, 0xCC, 0xCC),
            Color.FromRgb(0x76, 0x76, 0x76),
            Color.FromRgb(0xE7, 0x48, 0x56),
            Color.FromRgb(0x16, 0xC6, 0x0C),
            Color.FromRgb(0xF9, 0xF1, 0xA5),
            Color.FromRgb(0x3B, 0x78, 0xFF),
            Color.FromRgb(0xB4, 0x00, 0x9E),
            Color.FromRgb(0x61, 0xD6, 0xD6),
            Color.FromRgb(0xF2, 0xF2, 0xF2)
        ]);

    public static TerminalTheme SolarizedDark { get; } = new(
        "Solarized Dark",
        defaultFg: Color.FromRgb(0x83, 0x94, 0x96),
        defaultBg: Color.FromRgb(0x00, 0x2B, 0x36),
        cursor:    Color.FromRgb(0x93, 0xA1, 0xA1),
        selection: Color.FromArgb(0x66, 0x07, 0x36, 0x42),
        ansi16:
        [
            Color.FromRgb(0x07, 0x36, 0x42),
            Color.FromRgb(0xDC, 0x32, 0x2F),
            Color.FromRgb(0x85, 0x99, 0x00),
            Color.FromRgb(0xB5, 0x89, 0x00),
            Color.FromRgb(0x26, 0x8B, 0xD2),
            Color.FromRgb(0xD3, 0x36, 0x82),
            Color.FromRgb(0x2A, 0xA1, 0x98),
            Color.FromRgb(0xEE, 0xE8, 0xD5),
            Color.FromRgb(0x00, 0x2B, 0x36),
            Color.FromRgb(0xCB, 0x4B, 0x16),
            Color.FromRgb(0x58, 0x6E, 0x75),
            Color.FromRgb(0x65, 0x7B, 0x83),
            Color.FromRgb(0x83, 0x94, 0x96),
            Color.FromRgb(0x6C, 0x71, 0xC4),
            Color.FromRgb(0x93, 0xA1, 0xA1),
            Color.FromRgb(0xFD, 0xF6, 0xE3)
        ]);

    public static TerminalTheme Dracula { get; } = new(
        "Dracula",
        defaultFg: Color.FromRgb(0xF8, 0xF8, 0xF2),
        defaultBg: Color.FromRgb(0x28, 0x2A, 0x36),
        cursor:    Color.FromRgb(0xFF, 0x79, 0xC6),
        selection: Color.FromArgb(0x66, 0x44, 0x47, 0x5A),
        ansi16:
        [
            Color.FromRgb(0x21, 0x22, 0x2C),
            Color.FromRgb(0xFF, 0x55, 0x55),
            Color.FromRgb(0x50, 0xFA, 0x7B),
            Color.FromRgb(0xF1, 0xFA, 0x8C),
            Color.FromRgb(0xBD, 0x93, 0xF9),
            Color.FromRgb(0xFF, 0x79, 0xC6),
            Color.FromRgb(0x8B, 0xE9, 0xFD),
            Color.FromRgb(0xF8, 0xF8, 0xF2),
            Color.FromRgb(0x62, 0x72, 0xA4),
            Color.FromRgb(0xFF, 0x6E, 0x6E),
            Color.FromRgb(0x69, 0xFF, 0x94),
            Color.FromRgb(0xFF, 0xFF, 0xA5),
            Color.FromRgb(0xD6, 0xAC, 0xFF),
            Color.FromRgb(0xFF, 0x92, 0xDF),
            Color.FromRgb(0xA4, 0xFF, 0xFF),
            Color.FromRgb(0xFF, 0xFF, 0xFF)
        ]);

    public static TerminalTheme Monokai { get; } = new(
        "Monokai",
        defaultFg: Color.FromRgb(0xF8, 0xF8, 0xF2),
        defaultBg: Color.FromRgb(0x27, 0x28, 0x22),
        cursor:    Color.FromRgb(0xF8, 0xF8, 0xF0),
        selection: Color.FromArgb(0x66, 0x49, 0x48, 0x3E),
        ansi16:
        [
            Color.FromRgb(0x27, 0x28, 0x22),
            Color.FromRgb(0xF9, 0x26, 0x72),
            Color.FromRgb(0xA6, 0xE2, 0x2E),
            Color.FromRgb(0xF4, 0xBF, 0x75),
            Color.FromRgb(0x66, 0xD9, 0xEF),
            Color.FromRgb(0xAE, 0x81, 0xFF),
            Color.FromRgb(0xA1, 0xEF, 0xE4),
            Color.FromRgb(0xF8, 0xF8, 0xF2),
            Color.FromRgb(0x75, 0x71, 0x5E),
            Color.FromRgb(0xF9, 0x26, 0x72),
            Color.FromRgb(0xA6, 0xE2, 0x2E),
            Color.FromRgb(0xF4, 0xBF, 0x75),
            Color.FromRgb(0x66, 0xD9, 0xEF),
            Color.FromRgb(0xAE, 0x81, 0xFF),
            Color.FromRgb(0xA1, 0xEF, 0xE4),
            Color.FromRgb(0xF9, 0xF8, 0xF5)
        ]);
}

using System.Text;

namespace LiteConnect.Core.Recording;

/// <summary>
/// Stateful streaming formatter that converts raw SSH UTF-8/ANSI byte chunks into formatted log text.
/// </summary>
public interface ISessionLogFormatter
{
    /// <summary>Optional header written when the recording file is opened.</summary>
    string GetHeader();

    /// <summary>Formats a chunk of raw SSH stream bytes at the given <paramref name="timestamp"/>.</summary>
    string FormatChunk(ReadOnlySpan<byte> data, DateTimeOffset timestamp);

    /// <summary>Optional footer written when the recording file is closed.</summary>
    string GetFooter();
}

/// <summary>
/// Strips all ANSI/VT100 escape sequences (CSI, OSC, DCS, charset designation) and outputs clean plain text.
/// Stateful across chunk boundaries so split UTF-8 sequences or split CSI sequences never leak.
/// </summary>
public class PlainTextFormatter : ISessionLogFormatter
{
    private enum AnsiStripState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        StringIgnore,
        StringIgnoreEscape
    }

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _charBuf = new char[4096];
    private AnsiStripState _state = AnsiStripState.Ground;
    private bool _lastWasCr;

    public virtual string GetHeader() => string.Empty;

    public virtual string GetFooter() => string.Empty;

    public virtual string FormatChunk(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        return StripAnsiToCleanText(data);
    }

    protected string StripAnsiToCleanText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder(data.Length);

        while (!data.IsEmpty)
        {
            _decoder.Convert(data, _charBuf, flush: false, out int bytesUsed, out int charsUsed, out _);
            for (int i = 0; i < charsUsed; i++)
            {
                ProcessStripChar(_charBuf[i], sb);
            }
            data = data[bytesUsed..];
        }

        return sb.ToString();
    }

    private void ProcessStripChar(char ch, StringBuilder output)
    {
        switch (_state)
        {
            case AnsiStripState.Ground:
                if (ch == '\x1b')
                {
                    _lastWasCr = false;
                    _state = AnsiStripState.Escape;
                }
                else if (ch == '\r')
                {
                    _lastWasCr = true;
                }
                else if (ch == '\n')
                {
                    _lastWasCr = false;
                    output.Append('\n');
                }
                else if (ch == '\b')
                {
                    _lastWasCr = false;
                    if (output.Length > 0 && output[^1] != '\n')
                        output.Length--;
                }
                else if (ch == '\t' || ch >= 0x20)
                {
                    if (_lastWasCr)
                    {
                        // Standalone CR (e.g., progress bar overwrite) — treat as newline or reset
                        _lastWasCr = false;
                    }
                    if (ch != 0x7F)
                        output.Append(ch);
                }
                break;

            case AnsiStripState.Escape:
                if (ch == '[')
                    _state = AnsiStripState.Csi;
                else if (ch == ']')
                    _state = AnsiStripState.Osc;
                else if (ch is 'P' or 'X' or '^' or '_')
                    _state = AnsiStripState.StringIgnore;
                else if (ch is >= '\x20' and <= '\x2F')
                    _state = AnsiStripState.EscapeIntermediate;
                else
                    _state = AnsiStripState.Ground;
                break;

            case AnsiStripState.EscapeIntermediate:
                if (ch is >= '\x30' and <= '\x7E')
                    _state = AnsiStripState.Ground;
                break;

            case AnsiStripState.Csi:
                if (ch is >= '\x40' and <= '\x7E')
                    _state = AnsiStripState.Ground;
                break;

            case AnsiStripState.Osc:
                if (ch == '\x07')
                    _state = AnsiStripState.Ground;
                else if (ch == '\x1b')
                    _state = AnsiStripState.OscEscape;
                break;

            case AnsiStripState.OscEscape:
                _state = AnsiStripState.Ground;
                break;

            case AnsiStripState.StringIgnore:
                if (ch == '\x07')
                    _state = AnsiStripState.Ground;
                else if (ch == '\x1b')
                    _state = AnsiStripState.StringIgnoreEscape;
                break;

            case AnsiStripState.StringIgnoreEscape:
                _state = ch == '\\' ? AnsiStripState.Ground : AnsiStripState.StringIgnore;
                break;
        }
    }
}

/// <summary>
/// Strips ANSI escape sequences and prefixes every line with <c>[yyyy-MM-dd HH:mm:ss.fff] </c>.
/// Preserves partial line state across chunks so timestamps only appear at the beginning of lines.
/// </summary>
public sealed class TimestampedFormatter : PlainTextFormatter
{
    private bool _atStartOfLine = true;

    public override string FormatChunk(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        string clean = StripAnsiToCleanText(data);
        if (string.IsNullOrEmpty(clean))
            return string.Empty;

        string prefix = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] ";
        var sb = new StringBuilder(clean.Length + 32);

        for (int i = 0; i < clean.Length; i++)
        {
            char ch = clean[i];
            if (_atStartOfLine)
            {
                sb.Append(prefix);
                _atStartOfLine = false;
            }

            sb.Append(ch);

            if (ch == '\n')
            {
                _atStartOfLine = true;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Converts ANSI SGR color and text-attribute sequences into styled HTML <c>&lt;span style="..."&gt;</c>
/// elements inside a dark-themed <c>&lt;pre&gt;</c> container.
/// </summary>
public sealed class HtmlFormatter : ISessionLogFormatter
{
    private enum HtmlParseState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape
    }

    // Default Dark 16-color palette matching TerminalTheme.DefaultDark
    private static readonly string[] Ansi16Palette =
    [
        "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00",
        "#0037da", "#881798", "#3a96dd", "#cccccc",
        "#767676", "#e74856", "#16c60c", "#f9f1a5",
        "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2"
    ];

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _charBuf = new char[4096];
    private HtmlParseState _state = HtmlParseState.Ground;
    private readonly StringBuilder _csiBuilder = new(32);

    // Current SGR state
    private string? _fgCss;
    private string? _bgCss;
    private bool _bold;
    private bool _dim;
    private bool _italic;
    private bool _underline;
    private bool _inverse;
    private bool _strikethrough;

    private bool _spanOpen;
    private string _activeStyle = string.Empty;

    public string GetHeader() =>
        "<pre style=\"background:#1e1e1e;color:#d4d4d4;font-family:'Cascadia Mono',Consolas,monospace;font-size:13px;padding:12px;margin:0;\">\n";

    public string GetFooter()
    {
        if (_spanOpen)
        {
            _spanOpen = false;
            return "</span></pre>\n";
        }
        return "</pre>\n";
    }

    public string FormatChunk(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        if (data.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder(data.Length * 2);

        while (!data.IsEmpty)
        {
            _decoder.Convert(data, _charBuf, flush: false, out int bytesUsed, out int charsUsed, out _);
            for (int i = 0; i < charsUsed; i++)
            {
                ProcessHtmlChar(_charBuf[i], sb);
            }
            data = data[bytesUsed..];
        }

        return sb.ToString();
    }

    private void ProcessHtmlChar(char ch, StringBuilder sb)
    {
        switch (_state)
        {
            case HtmlParseState.Ground:
                if (ch == '\x1b')
                {
                    _state = HtmlParseState.Escape;
                }
                else if (ch == '\r')
                {
                    // Ignore CR before LF
                }
                else if (ch == '\n')
                {
                    if (_spanOpen && BuildCssStyle() != _activeStyle)
                    {
                        sb.Append("</span>");
                        _spanOpen = false;
                        _activeStyle = string.Empty;
                    }
                    sb.Append('\n');
                }
                else if (ch == '\t' || (ch >= 0x20 && ch != 0x7F))
                {
                    EnsureStyleSpan(sb);
                    switch (ch)
                    {
                        case '&': sb.Append("&amp;"); break;
                        case '<': sb.Append("&lt;"); break;
                        case '>': sb.Append("&gt;"); break;
                        default:  sb.Append(ch); break;
                    }
                }
                break;

            case HtmlParseState.Escape:
                if (ch == '[')
                {
                    _csiBuilder.Clear();
                    _state = HtmlParseState.Csi;
                }
                else if (ch == ']')
                {
                    _state = HtmlParseState.Osc;
                }
                else if (ch is >= '\x20' and <= '\x2F')
                {
                    _state = HtmlParseState.EscapeIntermediate;
                }
                else
                {
                    _state = HtmlParseState.Ground;
                }
                break;

            case HtmlParseState.EscapeIntermediate:
                if (ch is >= '\x30' and <= '\x7E')
                    _state = HtmlParseState.Ground;
                break;

            case HtmlParseState.Csi:
                if (ch is >= '\x40' and <= '\x7E')
                {
                    if (ch == 'm')
                        ApplySgrParameters(_csiBuilder.ToString());
                    _state = HtmlParseState.Ground;
                }
                else if (_csiBuilder.Length < 64)
                {
                    _csiBuilder.Append(ch);
                }
                break;

            case HtmlParseState.Osc:
                if (ch == '\x07')
                    _state = HtmlParseState.Ground;
                else if (ch == '\x1b')
                    _state = HtmlParseState.OscEscape;
                break;

            case HtmlParseState.OscEscape:
                _state = HtmlParseState.Ground;
                break;
        }
    }

    private void EnsureStyleSpan(StringBuilder sb)
    {
        string desiredStyle = BuildCssStyle();
        if (desiredStyle == _activeStyle)
            return;

        if (_spanOpen)
        {
            sb.Append("</span>");
            _spanOpen = false;
        }

        _activeStyle = desiredStyle;
        if (!string.IsNullOrEmpty(desiredStyle))
        {
            sb.Append("<span style=\"").Append(desiredStyle).Append("\">");
            _spanOpen = true;
        }
    }

    private string BuildCssStyle()
    {
        string? fg = _fgCss;
        string? bg = _bgCss;

        if (_inverse)
        {
            fg = _bgCss ?? "#1e1e1e";
            bg = _fgCss ?? "#d4d4d4";
        }

        var style = new StringBuilder(64);
        if (fg is not null)
            style.Append("color:").Append(fg).Append(';');
        if (bg is not null)
            style.Append("background-color:").Append(bg).Append(';');
        if (_bold)
            style.Append("font-weight:bold;");
        if (_dim)
            style.Append("opacity:0.7;");
        if (_italic)
            style.Append("font-style:italic;");
        if (_underline && _strikethrough)
            style.Append("text-decoration:underline line-through;");
        else if (_underline)
            style.Append("text-decoration:underline;");
        else if (_strikethrough)
            style.Append("text-decoration:line-through;");

        return style.ToString();
    }

    private void ApplySgrParameters(string rawParams)
    {
        if (string.IsNullOrWhiteSpace(rawParams))
        {
            ResetSgr();
            return;
        }

        string[] parts = rawParams.Split(';');
        var codes = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            codes[i] = int.TryParse(parts[i], out int v) ? v : 0;
        }

        int idx = 0;
        while (idx < codes.Length)
        {
            int code = codes[idx];
            switch (code)
            {
                case 0:
                    ResetSgr();
                    break;
                case 1:
                    _bold = true;
                    break;
                case 2:
                    _dim = true;
                    break;
                case 3:
                    _italic = true;
                    break;
                case 4:
                    _underline = true;
                    break;
                case 7:
                    _inverse = true;
                    break;
                case 9:
                    _strikethrough = true;
                    break;
                case 21 or 22:
                    _bold = false;
                    _dim = false;
                    break;
                case 23:
                    _italic = false;
                    break;
                case 24:
                    _underline = false;
                    break;
                case 27:
                    _inverse = false;
                    break;
                case 29:
                    _strikethrough = false;
                    break;

                case >= 30 and <= 37:
                    _fgCss = Ansi16Palette[code - 30];
                    break;
                case 39:
                    _fgCss = null;
                    break;

                case >= 40 and <= 47:
                    _bgCss = Ansi16Palette[code - 40];
                    break;
                case 49:
                    _bgCss = null;
                    break;

                case >= 90 and <= 97:
                    _fgCss = Ansi16Palette[(code - 90) + 8];
                    break;

                case >= 100 and <= 107:
                    _bgCss = Ansi16Palette[(code - 100) + 8];
                    break;

                case 38 or 48:
                {
                    bool isFg = code == 38;
                    if (idx + 1 < codes.Length)
                    {
                        int mode = codes[idx + 1];
                        if (mode == 5 && idx + 2 < codes.Length)
                        {
                            int colorIdx = Math.Clamp(codes[idx + 2], 0, 255);
                            string hex = Resolve256ColorHex(colorIdx);
                            if (isFg) _fgCss = hex; else _bgCss = hex;
                            idx += 2;
                        }
                        else if (mode == 2 && idx + 4 < codes.Length)
                        {
                            int r = Math.Clamp(codes[idx + 2], 0, 255);
                            int g = Math.Clamp(codes[idx + 3], 0, 255);
                            int b = Math.Clamp(codes[idx + 4], 0, 255);
                            string hex = $"#{r:x2}{g:x2}{b:x2}";
                            if (isFg) _fgCss = hex; else _bgCss = hex;
                            idx += 4;
                        }
                    }
                    break;
                }
            }
            idx++;
        }
    }

    private void ResetSgr()
    {
        _fgCss = null;
        _bgCss = null;
        _bold = false;
        _dim = false;
        _italic = false;
        _underline = false;
        _inverse = false;
        _strikethrough = false;
    }

    private static string Resolve256ColorHex(int index)
    {
        if (index < 16)
            return Ansi16Palette[index];

        if (index < 232)
        {
            int cube = index - 16;
            int rIdx = cube / 36;
            int gIdx = (cube % 36) / 6;
            int bIdx = cube % 6;
            int r = rIdx == 0 ? 0 : 55 + rIdx * 40;
            int g = gIdx == 0 ? 0 : 55 + gIdx * 40;
            int b = bIdx == 0 ? 0 : 55 + bIdx * 40;
            return $"#{r:x2}{g:x2}{b:x2}";
        }

        int gray = 8 + (index - 232) * 10;
        return $"#{gray:x2}{gray:x2}{gray:x2}";
    }
}

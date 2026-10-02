using System.Text;

namespace LiteConnect.Terminal;

/// <summary>
/// VT100 / xterm state-machine parser (modeled after <c>vt100.net/emu/dec_ansi_parser</c>).
/// Processes incoming UTF-8 bytes or characters and mutates a <see cref="TerminalBuffer"/>.
/// </summary>
public sealed class AnsiParser
{
    private enum ParserState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        OscString,
        IgnoreString
    }

    private readonly TerminalBuffer _buffer;
    private readonly Decoder _utf8Decoder;
    private readonly char[] _charDecodeBuf = new char[4096];

    private ParserState _state = ParserState.Ground;
    private readonly List<int> _csiParams = new(16);
    private int _currentParam = -1;
    private char _csiPrefix;       // '?', '>', '=' or '\0'
    private char _intermediate;    // '(', ')', '#', ' ', etc.
    private bool _oscEscSeen;
    private bool _ignoreEscSeen;
    private readonly StringBuilder _oscBuilder = new(128);

    private char _lastPrintedChar = ' ';
    private bool _useDecSpecialGraphicsG0;

    /// <summary>Fires when an <c>OSC 0;title</c> or <c>OSC 2;title</c> sequence updates the window/tab title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Fires when the terminal receives <c>BEL</c> (<c>0x07</c>).</summary>
    public event EventHandler? BellRang;

    /// <summary>Fires when the terminal must send a response back to the host (e.g., DSR <c>CSI 6n</c> or DA <c>CSI c</c>).</summary>
    public event EventHandler<string>? ResponseRequired;

    public AnsiParser(TerminalBuffer buffer, Encoding? encoding = null)
    {
        _buffer      = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _utf8Decoder = (encoding ?? Encoding.UTF8).GetDecoder();
    }

    /// <summary>
    /// Feeds raw bytes from the SSH stream through the character decoder into the VT100/xterm state machine.
    /// </summary>
    public void ProcessBytes(ReadOnlySpan<byte> data)
    {
        lock (_buffer.SyncRoot)
        {
            while (!data.IsEmpty)
            {
                _utf8Decoder.Convert(
                    data,
                    _charDecodeBuf,
                    flush: false,
                    out int bytesUsed,
                    out int charsUsed,
                    out _);

                for (int i = 0; i < charsUsed; i++)
                {
                    ProcessCharCore(_charDecodeBuf[i]);
                }

                data = data[bytesUsed..];
            }
        }
    }

    /// <summary>
    /// Feeds a decoded string directly into the VT100/xterm state machine.
    /// </summary>
    public void ProcessText(ReadOnlySpan<char> text)
    {
        lock (_buffer.SyncRoot)
        {
            for (int i = 0; i < text.Length; i++)
            {
                ProcessCharCore(text[i]);
            }
        }
    }

    private void ProcessCharCore(char ch)
    {
        // Handle OSC / IgnoreString before general C0 controls because OSC uses BEL (0x07) and ESC \
        if (_state == ParserState.OscString)
        {
            ProcessOscChar(ch);
            return;
        }

        if (_state == ParserState.IgnoreString)
        {
            ProcessIgnoreStringChar(ch);
            return;
        }

        // Execute C0 control characters anywhere (even mid-CSI, per DEC STD 070)
        if (ch < 0x20 || ch == 0x7F)
        {
            if (ch == '\x1b') // ESC
            {
                _state        = ParserState.Escape;
                _intermediate = '\0';
                return;
            }

            if (ch is '\x18' or '\x1a') // CAN / SUB cancel escape sequence
            {
                _state = ParserState.Ground;
                return;
            }

            ExecuteC0Control(ch);
            return;
        }

        switch (_state)
        {
            case ParserState.Ground:
                char mapped = _useDecSpecialGraphicsG0 ? MapDecSpecialGraphics(ch) : ch;
                _buffer.PutChar(mapped);
                _lastPrintedChar = mapped;
                break;

            case ParserState.Escape:
                ProcessEscape(ch);
                break;

            case ParserState.EscapeIntermediate:
                ProcessEscapeIntermediate(ch);
                break;

            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate:
                ProcessCsi(ch);
                break;
        }
    }

    // ── C0 Control Execution ────────────────────────────────────────────────────

    private void ExecuteC0Control(char ch)
    {
        switch (ch)
        {
            case '\x07': // BEL
                BellRang?.Invoke(this, EventArgs.Empty);
                break;

            case '\x08': // BS
                _buffer.Backspace();
                break;

            case '\x09': // HT
                _buffer.HorizontalTab();
                break;

            case '\x0A': // LF
            case '\x0B': // VT
            case '\x0C': // FF
                _buffer.LineFeed();
                break;

            case '\x0D': // CR
                _buffer.CarriageReturn();
                break;

            case '\x0E': // SO (Shift Out — G1 charset)
                break;

            case '\x0F': // SI (Shift In — G0 charset)
                break;
        }
    }

    // ── ESC State ───────────────────────────────────────────────────────────────

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[': // CSI
                _state        = ParserState.CsiEntry;
                _csiParams.Clear();
                _currentParam = -1;
                _csiPrefix    = '\0';
                _intermediate = '\0';
                break;

            case ']': // OSC
                _state      = ParserState.OscString;
                _oscEscSeen = false;
                _oscBuilder.Clear();
                break;

            case 'P': // DCS
            case 'X': // SOS
            case '^': // PM
            case '_': // APC
                _state         = ParserState.IgnoreString;
                _ignoreEscSeen = false;
                break;

            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case ' ':
                _intermediate = ch;
                _state        = ParserState.EscapeIntermediate;
                break;

            case '7': // DECSC — Save Cursor
                _buffer.SaveCursor();
                _state = ParserState.Ground;
                break;

            case '8': // DECRC — Restore Cursor
                _buffer.RestoreCursor();
                _state = ParserState.Ground;
                break;

            case 'D': // IND — Index (Line Feed)
                _buffer.LineFeed();
                _state = ParserState.Ground;
                break;

            case 'E': // NEL — Next Line
                _buffer.NewLine();
                _state = ParserState.Ground;
                break;

            case 'H': // HTS — Horizontal Tab Set
                _buffer.SetTabStopAtCursor();
                _state = ParserState.Ground;
                break;

            case 'M': // RI — Reverse Index
                _buffer.ReverseIndex();
                _state = ParserState.Ground;
                break;

            case 'c': // RIS — Full Reset
                _buffer.FullReset();
                _useDecSpecialGraphicsG0 = false;
                _state = ParserState.Ground;
                break;

            case '=': // DECKPAM — Application Keypad
            case '>': // DECKPNM — Normal Keypad
            default:
                _state = ParserState.Ground;
                break;
        }
    }

    private void ProcessEscapeIntermediate(char ch)
    {
        if (ch is >= '\x20' and <= '\x2F')
        {
            _intermediate = ch;
            return;
        }

        // Character set designation: ESC ( 0 = DEC Special Graphics, ESC ( B = US-ASCII
        if (_intermediate == '(')
        {
            _useDecSpecialGraphicsG0 = ch == '0';
        }

        _state = ParserState.Ground;
    }

    // ── CSI State ───────────────────────────────────────────────────────────────

    private void ProcessCsi(char ch)
    {
        if (_state == ParserState.CsiEntry && ch is '?' or '>' or '=' or '<')
        {
            _csiPrefix = ch;
            _state     = ParserState.CsiParam;
            return;
        }

        if (ch is >= '0' and <= '9')
        {
            _state = ParserState.CsiParam;
            _currentParam = (_currentParam < 0 ? 0 : _currentParam * 10) + (ch - '0');
            return;
        }

        if (ch is ';' or ':')
        {
            _state = ParserState.CsiParam;
            _csiParams.Add(_currentParam < 0 ? 0 : _currentParam);
            _currentParam = -1;
            return;
        }

        if (ch is >= '\x20' and <= '\x2F')
        {
            _intermediate = ch;
            _state        = ParserState.CsiIntermediate;
            return;
        }

        if (ch is >= '\x40' and <= '\x7E')
        {
            if (_currentParam >= 0 || _csiParams.Count > 0)
            {
                _csiParams.Add(_currentParam < 0 ? 0 : _currentParam);
            }

            DispatchCsi(ch);
            _state = ParserState.Ground;
        }
    }

    private int GetParam(int index, int defaultValue)
    {
        if (index >= _csiParams.Count)
            return defaultValue;
        int val = _csiParams[index];
        return val <= 0 && defaultValue > 0 ? defaultValue : val;
    }

    private int GetRawParam(int index, int defaultValue = 0)
    {
        return index < _csiParams.Count ? _csiParams[index] : defaultValue;
    }

    private void DispatchCsi(char finalByte)
    {
        if (_csiPrefix == '?')
        {
            DispatchDecPrivateCsi(finalByte);
            return;
        }

        if (_csiPrefix == '>')
        {
            if (finalByte == 'c')
            {
                // Secondary Device Attributes (DA2)
                ResponseRequired?.Invoke(this, "\x1b[>0;276;0c");
            }
            return;
        }

        switch (finalByte)
        {
            case 'A': // CUU — Cursor Up
                _buffer.MoveCursorUp(GetParam(0, 1));
                break;

            case 'B': // CUD — Cursor Down
            case 'e': // VPR
                _buffer.MoveCursorDown(GetParam(0, 1));
                break;

            case 'C': // CUF — Cursor Forward
            case 'a': // HPR
                _buffer.MoveCursorForward(GetParam(0, 1));
                break;

            case 'D': // CUB — Cursor Backward
                _buffer.MoveCursorBackward(GetParam(0, 1));
                break;

            case 'E': // CNL — Cursor Next Line
                _buffer.MoveCursorDown(GetParam(0, 1));
                _buffer.CarriageReturn();
                break;

            case 'F': // CPL — Cursor Preceding Line
                _buffer.MoveCursorUp(GetParam(0, 1));
                _buffer.CarriageReturn();
                break;

            case 'G': // CHA — Cursor Character Absolute (1-based column)
            case '`': // HPA
                _buffer.SetCursorColumn(GetParam(0, 1) - 1);
                break;

            case 'H': // CUP — Cursor Position (1-based row;col)
            case 'f': // HVP
                int row = GetParam(0, 1) - 1;
                int col = GetParam(1, 1) - 1;
                _buffer.SetCursorPosition(row, col);
                break;

            case 'I': // CHT — Cursor Forward Tabulation
                _buffer.HorizontalTab(GetParam(0, 1));
                break;

            case 'J': // ED — Erase in Display
                _buffer.EraseInDisplay(GetRawParam(0, 0));
                break;

            case 'K': // EL — Erase in Line
                _buffer.EraseInLine(GetRawParam(0, 0));
                break;

            case 'L': // IL — Insert Lines
                _buffer.InsertLines(GetParam(0, 1));
                break;

            case 'M': // DL — Delete Lines
                _buffer.DeleteLines(GetParam(0, 1));
                break;

            case 'P': // DCH — Delete Characters
                _buffer.DeleteCharacters(GetParam(0, 1));
                break;

            case 'S': // SU — Scroll Up
                _buffer.ScrollUp(GetParam(0, 1));
                break;

            case 'T': // SD — Scroll Down
                _buffer.ScrollDown(GetParam(0, 1));
                break;

            case 'X': // ECH — Erase Characters
                _buffer.EraseCharacters(GetParam(0, 1));
                break;

            case 'Z': // CBT — Cursor Backward Tabulation
                _buffer.BackwardTab(GetParam(0, 1));
                break;

            case '@': // ICH — Insert Blank Characters
                _buffer.InsertCharacters(GetParam(0, 1));
                break;

            case 'b': // REP — Repeat preceding graphic character
            {
                int repeatCount = Math.Clamp(GetParam(0, 1), 1, 4096);
                for (int i = 0; i < repeatCount; i++)
                    _buffer.PutChar(_lastPrintedChar);
                break;
            }

            case 'c': // DA — Send Device Attributes (Primary)
                ResponseRequired?.Invoke(this, "\x1b[?1;2c");
                break;

            case 'd': // VPA — Line Position Absolute (1-based row)
                _buffer.SetCursorRow(GetParam(0, 1) - 1);
                break;

            case 'g': // TBC — Tab Clear
                _buffer.ClearTabStop(GetRawParam(0, 0));
                break;

            case 'm': // SGR — Select Graphic Rendition
                ApplySgr();
                break;

            case 'n': // DSR — Device Status Report
            {
                int dsrMode = GetRawParam(0, 0);
                if (dsrMode == 5)
                {
                    ResponseRequired?.Invoke(this, "\x1b[0n");
                }
                else if (dsrMode == 6)
                {
                    int r1 = _buffer.CursorRow + 1;
                    int c1 = _buffer.CursorColumn + 1;
                    ResponseRequired?.Invoke(this, $"\x1b[{r1};{c1}R");
                }
                break;
            }

            case 'r': // DECSTBM — Set Top and Bottom Margins
                if (_intermediate == '\0')
                {
                    int top    = GetParam(0, 1) - 1;
                    int bottom = GetParam(1, _buffer.Rows) - 1;
                    _buffer.SetScrollRegion(top, bottom);
                }
                break;

            case 's': // SCOSC — Save Cursor
                if (_csiPrefix == '\0')
                    _buffer.SaveCursor();
                break;

            case 'u': // SCORC — Restore Cursor
                if (_csiPrefix == '\0')
                    _buffer.RestoreCursor();
                break;
        }
    }

    private void DispatchDecPrivateCsi(char finalByte)
    {
        if (finalByte is not ('h' or 'l'))
            return;

        bool enable = finalByte == 'h';

        for (int i = 0; i < _csiParams.Count; i++)
        {
            int mode = _csiParams[i];
            switch (mode)
            {
                case 1: // DECCKM — Application Cursor Keys
                    _buffer.ApplicationCursorKeys = enable;
                    break;

                case 6: // DECOM — Origin Mode
                    _buffer.OriginMode = enable;
                    _buffer.SetCursorPosition(0, 0);
                    break;

                case 7: // DECAWM — Auto-Wrap Mode
                    _buffer.AutoWrapMode = enable;
                    break;

                case 25: // DECTCEM — Text Cursor Enable Mode
                    _buffer.CursorVisible = enable;
                    break;

                case 47:
                case 1047: // Alternate Screen Buffer (without cursor save)
                    if (enable)
                        _buffer.SwitchToAlternateScreen(saveCursorAndClear: false);
                    else
                        _buffer.SwitchToPrimaryScreen(restoreCursor: false);
                    break;

                case 1048: // Save / Restore Cursor
                    if (enable)
                        _buffer.SaveCursor();
                    else
                        _buffer.RestoreCursor();
                    break;

                case 1049: // Save Cursor + Alternate Screen Buffer + Clear
                    if (enable)
                        _buffer.SwitchToAlternateScreen(saveCursorAndClear: true);
                    else
                        _buffer.SwitchToPrimaryScreen(restoreCursor: true);
                    break;

                case 2004: // Bracketed Paste Mode
                    _buffer.BracketedPasteMode = enable;
                    break;
            }
        }
    }

    // ── SGR (Colors & Attributes) ───────────────────────────────────────────────

    private void ApplySgr()
    {
        if (_csiParams.Count == 0)
        {
            _buffer.ResetAttributes();
            return;
        }

        for (int i = 0; i < _csiParams.Count; i++)
        {
            int code = _csiParams[i];
            switch (code)
            {
                case 0:
                    _buffer.ResetAttributes();
                    break;

                case 1:
                    _buffer.CurrentAttributes |= CellAttributes.Bold;
                    break;

                case 2:
                    _buffer.CurrentAttributes |= CellAttributes.Dim;
                    break;

                case 3:
                    _buffer.CurrentAttributes |= CellAttributes.Italic;
                    break;

                case 4:
                    _buffer.CurrentAttributes |= CellAttributes.Underline;
                    break;

                case 5:
                case 6:
                    _buffer.CurrentAttributes |= CellAttributes.Blink;
                    break;

                case 7:
                    _buffer.CurrentAttributes |= CellAttributes.Inverse;
                    break;

                case 8:
                    _buffer.CurrentAttributes |= CellAttributes.Hidden;
                    break;

                case 9:
                    _buffer.CurrentAttributes |= CellAttributes.Strikethrough;
                    break;

                case 21:
                case 22:
                    _buffer.CurrentAttributes &= ~(CellAttributes.Bold | CellAttributes.Dim);
                    break;

                case 23:
                    _buffer.CurrentAttributes &= ~CellAttributes.Italic;
                    break;

                case 24:
                    _buffer.CurrentAttributes &= ~CellAttributes.Underline;
                    break;

                case 25:
                    _buffer.CurrentAttributes &= ~CellAttributes.Blink;
                    break;

                case 27:
                    _buffer.CurrentAttributes &= ~CellAttributes.Inverse;
                    break;

                case 28:
                    _buffer.CurrentAttributes &= ~CellAttributes.Hidden;
                    break;

                case 29:
                    _buffer.CurrentAttributes &= ~CellAttributes.Strikethrough;
                    break;

                // Standard 8 foreground colors (30–37)
                case >= 30 and <= 37:
                    _buffer.CurrentForeground = TerminalColor.FromIndex((byte)(code - 30));
                    break;

                // Extended foreground color (38;5;n or 38;2;r;g;b)
                case 38:
                    i = ParseExtendedColor(i, isForeground: true);
                    break;

                case 39:
                    _buffer.CurrentForeground = TerminalColor.Default;
                    break;

                // Standard 8 background colors (40–47)
                case >= 40 and <= 47:
                    _buffer.CurrentBackground = TerminalColor.FromIndex((byte)(code - 40));
                    break;

                // Extended background color (48;5;n or 48;2;r;g;b)
                case 48:
                    i = ParseExtendedColor(i, isForeground: false);
                    break;

                case 49:
                    _buffer.CurrentBackground = TerminalColor.Default;
                    break;

                // Bright 8 foreground colors (90–97 → palette 8–15)
                case >= 90 and <= 97:
                    _buffer.CurrentForeground = TerminalColor.FromIndex((byte)(code - 90 + 8));
                    break;

                // Bright 8 background colors (100–107 → palette 8–15)
                case >= 100 and <= 107:
                    _buffer.CurrentBackground = TerminalColor.FromIndex((byte)(code - 100 + 8));
                    break;
            }
        }
    }

    private int ParseExtendedColor(int currentIndex, bool isForeground)
    {
        if (currentIndex + 1 >= _csiParams.Count)
            return currentIndex;

        int mode = _csiParams[currentIndex + 1];
        if (mode == 5 && currentIndex + 2 < _csiParams.Count)
        {
            byte idx = (byte)Math.Clamp(_csiParams[currentIndex + 2], 0, 255);
            var color = TerminalColor.FromIndex(idx);
            if (isForeground)
                _buffer.CurrentForeground = color;
            else
                _buffer.CurrentBackground = color;
            return currentIndex + 2;
        }

        if (mode == 2 && currentIndex + 4 < _csiParams.Count)
        {
            byte r = (byte)Math.Clamp(_csiParams[currentIndex + 2], 0, 255);
            byte g = (byte)Math.Clamp(_csiParams[currentIndex + 3], 0, 255);
            byte b = (byte)Math.Clamp(_csiParams[currentIndex + 4], 0, 255);
            var color = TerminalColor.FromRgb(r, g, b);
            if (isForeground)
                _buffer.CurrentForeground = color;
            else
                _buffer.CurrentBackground = color;
            return currentIndex + 4;
        }

        return currentIndex + 1;
    }

    // ── OSC & IgnoreString States ───────────────────────────────────────────────

    private void ProcessOscChar(char ch)
    {
        if (_oscEscSeen)
        {
            _oscEscSeen = false;
            if (ch == '\\') // ST (ESC \)
            {
                DispatchOsc(_oscBuilder.ToString());
                _state = ParserState.Ground;
                return;
            }
        }

        if (ch == '\x1b')
        {
            _oscEscSeen = true;
            return;
        }

        if (ch is '\x07' or '\x9c') // BEL or 8-bit ST
        {
            DispatchOsc(_oscBuilder.ToString());
            _state = ParserState.Ground;
            return;
        }

        if (_oscBuilder.Length < 1024)
            _oscBuilder.Append(ch);
    }

    private void DispatchOsc(string oscPayload)
    {
        int semi = oscPayload.IndexOf(';');
        if (semi <= 0)
            return;

        ReadOnlySpan<char> codeSpan = oscPayload.AsSpan(0, semi);
        if (int.TryParse(codeSpan, out int oscCode) && oscCode is 0 or 2)
        {
            string title = oscPayload[(semi + 1)..];
            TitleChanged?.Invoke(this, title);
        }
    }

    private void ProcessIgnoreStringChar(char ch)
    {
        if (_ignoreEscSeen)
        {
            _ignoreEscSeen = false;
            if (ch == '\\')
            {
                _state = ParserState.Ground;
                return;
            }
        }

        if (ch == '\x1b')
        {
            _ignoreEscSeen = true;
        }
        else if (ch is '\x07' or '\x9c')
        {
            _state = ParserState.Ground;
        }
    }

    // ── DEC Special Graphics Character Set (VT100 line drawing) ─────────────────

    private static char MapDecSpecialGraphics(char ch) => ch switch
    {
        'j' => '┘',
        'k' => '┐',
        'l' => '┌',
        'm' => '└',
        'n' => '┼',
        'q' => '─',
        't' => '├',
        'u' => '┤',
        'v' => '┴',
        'w' => '┬',
        'x' => '│',
        'a' => '▒',
        '`' => '◆',
        'f' => '°',
        'g' => '±',
        '~' => '·',
        _   => ch
    };
}

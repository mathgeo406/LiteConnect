using System.Globalization;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteConnect.Core.Models;

// Avoid ambiguity between WPF and WinForms types since UseWindowsForms may be enabled in consumers
using WpfClipboard = System.Windows.Clipboard;
using WpfCursors   = System.Windows.Input.Cursors;
using WpfPoint     = System.Windows.Point;

namespace LiteConnect.Terminal;

/// <summary>
/// Event arguments raised when the terminal grid dimensions change due to a control resize.
/// </summary>
public sealed class TerminalResizeEventArgs(int columns, int rows, int pixelWidth, int pixelHeight) : EventArgs
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
    public int PixelWidth { get; } = pixelWidth;
    public int PixelHeight { get; } = pixelHeight;
}

/// <summary>
/// Hardware-accelerated WPF custom control that renders a <see cref="TerminalBuffer"/>
/// using <see cref="DrawingContext"/>, handles keyboard/mouse input, selection, copy/paste,
/// scrollback navigation, and auto-resize (<c>SIGWINCH</c>).
/// </summary>
public sealed class TerminalControl : FrameworkElement
{
    private const double PaddingX = 6.0;
    private const double PaddingY = 4.0;

    private readonly TerminalBuffer _buffer;
    private readonly AnsiParser _parser;
    private readonly DispatcherTimer _cursorBlinkTimer;

    private FontFamily _fontFamily = new("Cascadia Mono, Consolas, Courier New");
    private Typeface _regularTypeface;
    private Typeface _boldTypeface;
    private Typeface _italicTypeface;
    private Typeface _boldItalicTypeface;

    private double _fontSize = 13.0;
    private double _cellWidth = 8.0;
    private double _cellHeight = 17.0;
    private double _pixelsPerDip = 1.0;

    private TerminalTheme _theme = TerminalTheme.DefaultDark;
    private string _cursorStyle = "Block"; // "Block", "Underline", "Bar"
    private bool _cursorBlinkEnabled = true;
    private bool _cursorBlinkPhase = true;
    private string _bellAction = "Visual"; // "Visual", "Sound", "None"
    private bool _visualBellActive;

    private bool _autoSizeColumnsRows = true;
    private int _fixedColumns;
    private int _fixedRows;

    // Scrollback offset (0 = live bottom, > 0 = lines scrolled up into scrollback)
    private int _scrollOffset;

    // Mouse selection coordinates (viewport row/col)
    private bool _isSelecting;
    private bool _hasSelection;
    private (int Row, int Col) _selStart;
    private (int Row, int Col) _selEnd;

    // Render coalescing flag so bursts of SSH data trigger at most 1 UI render per frame
    private int _renderScheduled;

    public TerminalBuffer Buffer => _buffer;
    public AnsiParser Parser => _parser;

    /// <summary>Fires when user keyboard input or terminal query responses need to be written to the SSH stream.</summary>
    public event EventHandler<byte[]>? InputReceived;

    /// <summary>Fires when the terminal grid dimensions change (<c>SIGWINCH</c>).</summary>
    public event EventHandler<TerminalResizeEventArgs>? TerminalResized;

    /// <summary>Fires when an <c>OSC 0;title</c> sequence changes the terminal title.</summary>
    public event EventHandler<string>? TitleChanged;

    public Encoding TextEncoding { get; private set; } = Encoding.UTF8;

    public TerminalControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = WpfCursors.IBeam;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _regularTypeface    = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface       = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);
        _italicTypeface     = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalicTypeface = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Bold,   FontStretches.Normal);

        _buffer = new TerminalBuffer(80, 24, 10_000);
        _parser = new AnsiParser(_buffer, TextEncoding);

        _parser.TitleChanged     += (_, title) => Dispatcher.BeginInvoke(() => TitleChanged?.Invoke(this, title));
        _parser.BellRang         += (_, _)     => Dispatcher.BeginInvoke(HandleBell);
        _parser.ResponseRequired += (_, resp)  => SendTextToStream(resp);

        _cursorBlinkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _cursorBlinkTimer.Tick += (_, _) =>
        {
            if (!_cursorBlinkEnabled || !IsKeyboardFocused)
                return;
            _cursorBlinkPhase = !_cursorBlinkPhase;
            InvalidateVisual();
        };

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;

        BuildContextMenu();
        RecalculateFontMetrics();
    }

    // ── Configuration from SshConnectionSettings ────────────────────────────────

    public void ApplySettings(SshConnectionSettings? settings)
    {
        if (settings is null)
            return;

        if (!string.IsNullOrWhiteSpace(settings.Encoding))
        {
            try { TextEncoding = Encoding.GetEncoding(settings.Encoding); }
            catch { TextEncoding = Encoding.UTF8; }
        }

        if (settings.ScrollbackLines is > 0)
            _buffer.MaxScrollbackLines = settings.ScrollbackLines.Value;

        string fontName = !string.IsNullOrWhiteSpace(settings.FontFamily)
            ? settings.FontFamily
            : "Cascadia Mono";
        _fontFamily = new FontFamily($"{fontName}, Cascadia Mono, Consolas, Courier New");
        _regularTypeface    = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface       = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);
        _italicTypeface     = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalicTypeface = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Bold,   FontStretches.Normal);

        if (settings.FontSize is >= 8 and <= 48)
            _fontSize = settings.FontSize.Value;

        _theme              = TerminalTheme.FromName(settings.ColorScheme);
        _cursorStyle        = settings.CursorStyle ?? "Block";
        _cursorBlinkEnabled = settings.CursorBlink ?? true;
        _bellAction         = settings.BellAction  ?? "Visual";

        if (settings.Columns is > 0 && settings.Rows is > 0)
        {
            _autoSizeColumnsRows = false;
            _fixedColumns        = settings.Columns.Value;
            _fixedRows           = settings.Rows.Value;
            lock (_buffer.SyncRoot)
            {
                _buffer.Resize(_fixedColumns, _fixedRows);
            }
        }
        else
        {
            _autoSizeColumnsRows = true;
        }

        RecalculateFontMetrics();
        InvalidateVisual();
    }

    // ── Data ingestion from SSH stream ──────────────────────────────────────────

    /// <summary>
    /// Thread-safe method called by <c>SshSession</c> when raw bytes arrive from the remote host.
    /// </summary>
    public void FeedBytes(ReadOnlySpan<byte> data)
    {
        _parser.ProcessBytes(data);

        // Snap scrollback to live bottom on new output unless user is actively dragging selection
        if (!_isSelecting)
            _scrollOffset = 0;

        RequestRender();
    }

    public void FeedText(string text)
    {
        _parser.ProcessText(text);
        RequestRender();
    }

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                Interlocked.Exchange(ref _renderScheduled, 0);
                InvalidateVisual();
            });
        }
    }

    // ── Font Metrics & Sizing ───────────────────────────────────────────────────

    private void RecalculateFontMetrics()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_pixelsPerDip <= 0) _pixelsPerDip = 1.0;

        var sample = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _regularTypeface,
            _fontSize,
            Brushes.White,
            _pixelsPerDip);

        _cellWidth  = Math.Max(4.0, Math.Ceiling(sample.WidthIncludingTrailingWhitespace));
        _cellHeight = Math.Max(8.0, Math.Ceiling(sample.Height));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RecalculateFontMetrics();
        UpdateGridDimensionsFromSize(RenderSize);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGridDimensionsFromSize(sizeInfo.NewSize);
        InvalidateVisual();
    }

    private void UpdateGridDimensionsFromSize(Size size)
    {
        if (size.Width <= PaddingX * 2 || size.Height <= PaddingY * 2)
            return;

        int cols = _autoSizeColumnsRows
            ? Math.Max(10, (int)Math.Floor((size.Width - PaddingX * 2) / _cellWidth))
            : _fixedColumns;
        int rows = _autoSizeColumnsRows
            ? Math.Max(4, (int)Math.Floor((size.Height - PaddingY * 2) / _cellHeight))
            : _fixedRows;

        bool changed = false;
        lock (_buffer.SyncRoot)
        {
            if (cols != _buffer.Columns || rows != _buffer.Rows)
            {
                _buffer.Resize(cols, rows);
                changed = true;
            }
        }

        if (changed)
        {
            TerminalResized?.Invoke(
                this,
                new TerminalResizeEventArgs(cols, rows, (int)size.Width, (int)size.Height));
        }
    }

    // ── Rendering (DrawingContext) ──────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // 1. Background fill
        dc.DrawRectangle(_theme.DefaultBackgroundBrush, null, bounds);

        lock (_buffer.SyncRoot)
        {
            int rows = _buffer.Rows;
            int cols = _buffer.Columns;

            for (int r = 0; r < rows; r++)
            {
                double y = PaddingY + r * _cellHeight;
                if (y + _cellHeight > bounds.Height + _cellHeight)
                    break;

                var rowCells = _buffer.GetViewportRow(r, _scrollOffset);
                if (rowCells.IsEmpty)
                    continue;

                // 2. Non-default cell backgrounds
                int c = 0;
                while (c < cols && c < rowCells.Length)
                {
                    var cell = rowCells[c];
                    bool inverse = cell.Attributes.HasFlag(CellAttributes.Inverse);
                    var bgBrush = inverse
                        ? _theme.ResolveForegroundBrush(cell.Foreground, cell.Attributes)
                        : _theme.ResolveBackgroundBrush(cell.Background);

                    if (!ReferenceEquals(bgBrush, _theme.DefaultBackgroundBrush))
                    {
                        int runEnd = c + 1;
                        while (runEnd < cols && runEnd < rowCells.Length)
                        {
                            var next = rowCells[runEnd];
                            bool nextInv = next.Attributes.HasFlag(CellAttributes.Inverse);
                            var nextBg = nextInv
                                ? _theme.ResolveForegroundBrush(next.Foreground, next.Attributes)
                                : _theme.ResolveBackgroundBrush(next.Background);
                            if (!ReferenceEquals(nextBg, bgBrush))
                                break;
                            runEnd++;
                        }

                        var bgRect = new Rect(
                            PaddingX + c * _cellWidth,
                            y,
                            (runEnd - c) * _cellWidth,
                            _cellHeight);
                        dc.DrawRectangle(bgBrush, null, bgRect);
                        c = runEnd;
                    }
                    else
                    {
                        c++;
                    }
                }

                // 3. Selection highlight for this row
                if (_hasSelection)
                {
                    DrawRowSelection(dc, r, y, cols);
                }

                // 4. Foreground text runs grouped by style & color
                c = 0;
                var runBuilder = new StringBuilder(cols);
                while (c < cols && c < rowCells.Length)
                {
                    var first = rowCells[c];
                    char firstCh = first.Character <= ' ' ? ' ' : first.Character;

                    if (firstCh == ' ' && !first.Attributes.HasFlag(CellAttributes.Underline) && !first.Attributes.HasFlag(CellAttributes.Strikethrough))
                    {
                        c++;
                        continue;
                    }

                    bool inv = first.Attributes.HasFlag(CellAttributes.Inverse);
                    var fgBrush = inv
                        ? _theme.ResolveBackgroundBrush(first.Background)
                        : _theme.ResolveForegroundBrush(first.Foreground, first.Attributes);
                    var attrs = first.Attributes;

                    runBuilder.Clear();
                    runBuilder.Append(firstCh);

                    int runStart = c;
                    c++;

                    while (c < cols && c < rowCells.Length)
                    {
                        var next = rowCells[c];
                        bool nextInv = next.Attributes.HasFlag(CellAttributes.Inverse);
                        var nextFg = nextInv
                            ? _theme.ResolveBackgroundBrush(next.Background)
                            : _theme.ResolveForegroundBrush(next.Foreground, next.Attributes);

                        if (!ReferenceEquals(nextFg, fgBrush) || next.Attributes != attrs)
                            break;

                        char ch = next.Character <= ' ' ? ' ' : next.Character;
                        runBuilder.Append(ch);
                        c++;
                    }

                    string runText = runBuilder.ToString().TrimEnd(' ');
                    if (runText.Length > 0)
                    {
                        var typeface = SelectTypeface(attrs);
                        var ft = new FormattedText(
                            runText,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            _fontSize,
                            fgBrush,
                            _pixelsPerDip);

                        dc.DrawText(ft, new WpfPoint(PaddingX + runStart * _cellWidth, y));
                    }

                    if (attrs.HasFlag(CellAttributes.Underline))
                    {
                        double ux = PaddingX + runStart * _cellWidth;
                        double uw = (c - runStart) * _cellWidth;
                        double uy = y + _cellHeight - 2.0;
                        dc.DrawRectangle(fgBrush, null, new Rect(ux, uy, uw, 1.0));
                    }
                }
            }

            // 5. Render Cursor (only when viewing live screen at scrollOffset == 0)
            if (_scrollOffset == 0 && _buffer.CursorVisible && (!_cursorBlinkEnabled || _cursorBlinkPhase || !IsKeyboardFocused))
            {
                DrawCursor(dc);
            }

            // 6. Scrollback position indicator bar
            if (_buffer.ScrollbackCount > 0 && !_buffer.IsAlternateScreen)
            {
                DrawScrollbarOverlay(dc, bounds);
            }
        }

        // 7. Visual Bell flash overlay
        if (_visualBellActive)
        {
            var flashBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            flashBrush.Freeze();
            dc.DrawRectangle(flashBrush, null, bounds);
        }
    }

    private Typeface SelectTypeface(CellAttributes attrs)
    {
        bool bold   = attrs.HasFlag(CellAttributes.Bold);
        bool italic = attrs.HasFlag(CellAttributes.Italic);
        return (bold, italic) switch
        {
            (true, true)   => _boldItalicTypeface,
            (true, false)  => _boldTypeface,
            (false, true)  => _italicTypeface,
            (false, false) => _regularTypeface
        };
    }

    private void DrawCursor(DrawingContext dc)
    {
        double cx = PaddingX + _buffer.CursorColumn * _cellWidth;
        double cy = PaddingY + _buffer.CursorRow * _cellHeight;

        if (!IsKeyboardFocused)
        {
            var pen = new Pen(_theme.CursorBrush, 1.0);
            pen.Freeze();
            dc.DrawRectangle(null, pen, new Rect(cx + 0.5, cy + 0.5, _cellWidth - 1.0, _cellHeight - 1.0));
            return;
        }

        switch (_cursorStyle)
        {
            case "Underline":
                dc.DrawRectangle(_theme.CursorBrush, null, new Rect(cx, cy + _cellHeight - 2.5, _cellWidth, 2.5));
                break;

            case "Bar":
                dc.DrawRectangle(_theme.CursorBrush, null, new Rect(cx, cy, 2.0, _cellHeight));
                break;

            default: // Block
                dc.DrawRectangle(_theme.CursorBrush, null, new Rect(cx, cy, _cellWidth, _cellHeight));
                var cell = _buffer.GetCell(_buffer.CursorRow, _buffer.CursorColumn);
                if (cell.Character > ' ')
                {
                    var ft = new FormattedText(
                        cell.Character.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        SelectTypeface(cell.Attributes),
                        _fontSize,
                        _theme.DefaultBackgroundBrush,
                        _pixelsPerDip);
                    dc.DrawText(ft, new WpfPoint(cx, cy));
                }
                break;
        }
    }

    private void DrawRowSelection(DrawingContext dc, int row, double y, int cols)
    {
        var (start, end) = GetNormalizedSelection();
        if (row < start.Row || row > end.Row)
            return;

        int cStart = row == start.Row ? start.Col : 0;
        int cEnd   = row == end.Row   ? end.Col   : cols - 1;

        cStart = Math.Clamp(cStart, 0, cols - 1);
        cEnd   = Math.Clamp(cEnd,   0, cols - 1);

        if (cEnd >= cStart)
        {
            var rect = new Rect(
                PaddingX + cStart * _cellWidth,
                y,
                (cEnd - cStart + 1) * _cellWidth,
                _cellHeight);
            dc.DrawRectangle(_theme.SelectionBrush, null, rect);
        }
    }

    private void DrawScrollbarOverlay(DrawingContext dc, Rect bounds)
    {
        int totalLines = _buffer.ScrollbackCount + _buffer.Rows;
        if (totalLines <= _buffer.Rows)
            return;

        double trackHeight = bounds.Height - 8.0;
        double thumbHeight = Math.Max(20.0, trackHeight * (_buffer.Rows / (double)totalLines));
        double scrollRatio = 1.0 - (_scrollOffset / (double)_buffer.ScrollbackCount);
        double thumbY      = 4.0 + (trackHeight - thumbHeight) * Math.Clamp(scrollRatio, 0.0, 1.0);

        var thumbBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x85, 0x85, 0x85));
        thumbBrush.Freeze();
        dc.DrawRoundedRectangle(thumbBrush, null, new Rect(bounds.Width - 6.0, thumbY, 4.0, thumbHeight), 2.0, 2.0);
    }

    // ── Lifecycle & Bell ────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cursorBlinkTimer.Start();
        Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cursorBlinkTimer.Stop();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _cursorBlinkPhase = true;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private void HandleBell()
    {
        if (_bellAction.Equals("Sound", StringComparison.OrdinalIgnoreCase))
        {
            SystemSounds.Beep.Play();
        }
        else if (_bellAction.Equals("Visual", StringComparison.OrdinalIgnoreCase))
        {
            _visualBellActive = true;
            InvalidateVisual();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _visualBellActive = false;
                InvalidateVisual();
            };
            timer.Start();
        }
    }

    // ── Mouse Selection, Copy/Paste & Scrollback ────────────────────────────────

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+Shift+C" };
        copyItem.Click += (_, _) => CopySelectionToClipboard();

        var pasteItem = new MenuItem { Header = "Paste", InputGestureText = "Right-Click / Ctrl+Shift+V" };
        pasteItem.Click += (_, _) => PasteFromClipboard();

        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) => SelectAll();

        var clearScrollbackItem = new MenuItem { Header = "Clear Scrollback" };
        clearScrollbackItem.Click += (_, _) =>
        {
            lock (_buffer.SyncRoot)
            {
                _buffer.EraseInDisplay(3);
                _buffer.SetCursorPosition(0, 0);
            }
            _scrollOffset = 0;
            InvalidateVisual();
        };

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAllItem);
        menu.Items.Add(clearScrollbackItem);

        ContextMenu = menu;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var pos = PointToCell(e.GetPosition(this));
        _isSelecting  = true;
        _hasSelection = false;
        _selStart     = pos;
        _selEnd       = pos;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isSelecting)
            return;

        var pos = PointToCell(e.GetPosition(this));
        if (pos != _selEnd)
        {
            _selEnd       = pos;
            _hasSelection = _selStart != _selEnd;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // Right-click pastes clipboard directly (classic PuTTY / Windows Terminal UX) unless Shift is held for context menu
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        lock (_buffer.SyncRoot)
        {
            if (_buffer.IsAlternateScreen)
            {
                // In alternate screen (vim/less/htop), send Up/Down cursor keys
                string seq = e.Delta > 0
                    ? (_buffer.ApplicationCursorKeys ? "\x1bOA" : "\x1b[A")
                    : (_buffer.ApplicationCursorKeys ? "\x1bOB" : "\x1b[B");
                SendTextToStream(seq + seq + seq);
                e.Handled = true;
                return;
            }

            int step = e.Delta > 0 ? 3 : -3;
            _scrollOffset = Math.Clamp(_scrollOffset + step, 0, _buffer.ScrollbackCount);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private (int Row, int Col) PointToCell(WpfPoint pt)
    {
        lock (_buffer.SyncRoot)
        {
            int col = (int)Math.Floor((pt.X - PaddingX) / _cellWidth);
            int row = (int)Math.Floor((pt.Y - PaddingY) / _cellHeight);
            col = Math.Clamp(col, 0, Math.Max(0, _buffer.Columns - 1));
            row = Math.Clamp(row, 0, Math.Max(0, _buffer.Rows - 1));
            return (row, col);
        }
    }

    private ((int Row, int Col) Start, (int Row, int Col) End) GetNormalizedSelection()
    {
        if (_selStart.Row < _selEnd.Row || (_selStart.Row == _selEnd.Row && _selStart.Col <= _selEnd.Col))
            return (_selStart, _selEnd);
        return (_selEnd, _selStart);
    }

    public void CopySelectionToClipboard()
    {
        if (!_hasSelection)
            return;

        string selectedText;
        lock (_buffer.SyncRoot)
        {
            var (start, end) = GetNormalizedSelection();
            selectedText = _buffer.ExtractViewportText(start.Row, start.Col, end.Row, end.Col, _scrollOffset);
        }

        if (!string.IsNullOrEmpty(selectedText))
        {
            try { WpfClipboard.SetText(selectedText); } catch { }
        }

        _hasSelection = false;
        InvalidateVisual();
    }

    public void PasteFromClipboard()
    {
        try
        {
            if (!WpfClipboard.ContainsText())
                return;

            string text = WpfClipboard.GetText();
            if (string.IsNullOrEmpty(text))
                return;

            // Normalize CRLF -> CR for terminal input
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');

            bool bracketed;
            lock (_buffer.SyncRoot)
            {
                bracketed = _buffer.BracketedPasteMode;
            }

            if (bracketed)
                SendTextToStream($"\x1b[200~{text}\x1b[201~");
            else
                SendTextToStream(text);
        }
        catch { }
    }

    public void SelectAll()
    {
        lock (_buffer.SyncRoot)
        {
            _selStart     = (0, 0);
            _selEnd       = (_buffer.Rows - 1, _buffer.Columns - 1);
            _hasSelection = true;
        }
        InvalidateVisual();
    }

    // ── Keyboard Input Handling ─────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var modifiers = Keyboard.Modifiers;
        bool ctrl  = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift)   != 0;
        bool alt   = (modifiers & ModifierKeys.Alt)     != 0;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Local scrollback navigation via Shift+PageUp / Shift+PageDown
        if (shift && !ctrl && !alt && key is Key.PageUp or Key.PageDown)
        {
            lock (_buffer.SyncRoot)
            {
                int page = Math.Max(1, _buffer.Rows - 2);
                int delta = key == Key.PageUp ? page : -page;
                _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, _buffer.ScrollbackCount);
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Copy / Paste shortcuts:
        // Ctrl+Shift+C always copies; Ctrl+C copies when text is selected, otherwise sends SIGINT (0x03)
        if (ctrl && key == Key.C && (shift || _hasSelection))
        {
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && key == Key.V && shift)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        // Ctrl+A..Z control characters (when not Shift-modified)
        if (ctrl && !alt && !shift && key is >= Key.A and <= Key.Z)
        {
            byte ctrlByte = (byte)(key - Key.A + 1);
            SendBytesToStream([ctrlByte]);
            e.Handled = true;
            return;
        }

        bool appCursor;
        lock (_buffer.SyncRoot)
        {
            appCursor = _buffer.ApplicationCursorKeys;
        }

        string? escapeSeq = MapSpecialKeyToEscapeSequence(key, appCursor, shift, ctrl, alt);
        if (escapeSeq is not null)
        {
            _scrollOffset = 0;
            SendTextToStream(escapeSeq);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        if (string.IsNullOrEmpty(e.Text))
            return;

        // Ignore control chars already handled in OnPreviewKeyDown
        if (e.Text.Length == 1 && e.Text[0] < 0x20 && e.Text[0] is not ('\r' or '\t' or '\b'))
            return;

        _scrollOffset = 0;
        _hasSelection = false;
        _cursorBlinkPhase = true;

        SendTextToStream(e.Text);
        e.Handled = true;
    }

    private static string? MapSpecialKeyToEscapeSequence(
        Key key,
        bool appCursor,
        bool shift,
        bool ctrl,
        bool alt)
    {
        return key switch
        {
            Key.Enter     => "\r",
            Key.Back      => "\x7f",
            Key.Tab       => shift ? "\x1b[Z" : "\t",
            Key.Escape    => "\x1b",

            Key.Up        => appCursor ? "\x1bOA" : "\x1b[A",
            Key.Down      => appCursor ? "\x1bOB" : "\x1b[B",
            Key.Right     => ctrl ? "\x1b[1;5C" : (appCursor ? "\x1bOC" : "\x1b[C"),
            Key.Left      => ctrl ? "\x1b[1;5D" : (appCursor ? "\x1bOD" : "\x1b[D"),

            Key.Home      => appCursor ? "\x1bOH" : "\x1b[H",
            Key.End       => appCursor ? "\x1bOF" : "\x1b[F",
            Key.Insert    => "\x1b[2~",
            Key.Delete    => "\x1b[3~",
            Key.PageUp    => "\x1b[5~",
            Key.PageDown  => "\x1b[6~",

            Key.F1        => "\x1bOP",
            Key.F2        => "\x1bOQ",
            Key.F3        => "\x1bOR",
            Key.F4        => "\x1bOS",
            Key.F5        => "\x1b[15~",
            Key.F6        => "\x1b[17~",
            Key.F7        => "\x1b[18~",
            Key.F8        => "\x1b[19~",
            Key.F9        => "\x1b[20~",
            Key.F10       => "\x1b[21~",
            Key.F11       => "\x1b[23~",
            Key.F12       => "\x1b[24~",
            _             => null
        };
    }

    private void SendTextToStream(string text)
    {
        byte[] bytes = TextEncoding.GetBytes(text);
        SendBytesToStream(bytes);
    }

    private void SendBytesToStream(byte[] bytes)
    {
        InputReceived?.Invoke(this, bytes);
    }
}

using System.Text;

namespace LiteConnect.Terminal;

/// <summary>
/// Maintains the VT100/xterm 2D character grid (primary + alternate screen buffers),
/// configurable circular scrollback buffer, cursor state, scroll region (<c>DECSTBM</c>),
/// and tab stops. Thread-safe via <see cref="SyncRoot"/> when accessed concurrently by the
/// SSH reader loop and the WPF render pass.
/// </summary>
public sealed class TerminalBuffer
{
    private const int DefaultTabWidth = 8;

    private TerminalCell[][] _primaryGrid;
    private TerminalCell[][] _alternateGrid;
    private TerminalCell[][] _activeGrid;

    private readonly List<TerminalCell[]> _scrollback = [];
    private readonly HashSet<int> _tabStops = [];
    private int _maxScrollbackLines;

    private bool _pendingWrap;

    // Saved cursor state (separate per screen buffer, as in xterm)
    private SavedCursorState _primarySavedCursor;
    private SavedCursorState _alternateSavedCursor;

    private record struct SavedCursorState(
        int Row,
        int Col,
        TerminalColor Foreground,
        TerminalColor Background,
        CellAttributes Attributes,
        bool OriginMode,
        bool AutoWrapMode);

    /// <summary>Synchronization lock object for thread-safe buffer mutations and rendering.</summary>
    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public int MaxScrollbackLines
    {
        get => _maxScrollbackLines;
        set
        {
            _maxScrollbackLines = Math.Clamp(value, 0, 200_000);
            TrimScrollback();
        }
    }

    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; set; } = true;
    public bool ApplicationCursorKeys { get; set; }
    public bool AutoWrapMode { get; set; } = true;
    public bool OriginMode { get; set; }
    public bool BracketedPasteMode { get; set; }
    public bool IsAlternateScreen { get; private set; }

    /// <summary>Top row of the active scrolling region (0-based, inclusive).</summary>
    public int ScrollTop { get; private set; }

    /// <summary>Bottom row of the active scrolling region (0-based, inclusive).</summary>
    public int ScrollBottom { get; private set; }

    public TerminalColor CurrentForeground { get; set; } = TerminalColor.Default;
    public TerminalColor CurrentBackground { get; set; } = TerminalColor.Default;
    public CellAttributes CurrentAttributes { get; set; } = CellAttributes.None;

    public int ScrollbackCount => _scrollback.Count;

    public TerminalBuffer(int columns = 80, int rows = 24, int maxScrollbackLines = 10_000)
    {
        Columns = Math.Max(2, columns);
        Rows    = Math.Max(2, rows);
        _maxScrollbackLines = Math.Clamp(maxScrollbackLines, 0, 200_000);

        _primaryGrid   = CreateGrid(Rows, Columns);
        _alternateGrid = CreateGrid(Rows, Columns);
        _activeGrid    = _primaryGrid;

        ScrollTop    = 0;
        ScrollBottom = Rows - 1;

        ResetDefaultTabStops();
    }

    // ── Grid allocation helpers ─────────────────────────────────────────────────

    private static TerminalCell[][] CreateGrid(int rows, int cols)
    {
        var grid = new TerminalCell[rows][];
        for (int r = 0; r < rows; r++)
            grid[r] = CreateBlankRow(cols, TerminalColor.Default, TerminalColor.Default);
        return grid;
    }

    private static TerminalCell[] CreateBlankRow(int cols, TerminalColor fg, TerminalColor bg)
    {
        var row = new TerminalCell[cols];
        var blank = TerminalCell.CreateBlank(fg, bg);
        Array.Fill(row, blank);
        return row;
    }

    private TerminalCell CurrentBlankCell =>
        TerminalCell.CreateBlank(CurrentForeground, CurrentBackground, CurrentAttributes);

    // ── Character output & wrapping ─────────────────────────────────────────────

    /// <summary>
    /// Writes a printable character at the current cursor position, applying VT100 deferred line wrapping.
    /// </summary>
    public void PutChar(char ch)
    {
        if (ch < ' ')
            return;

        if (_pendingWrap)
        {
            if (AutoWrapMode)
            {
                CursorColumn = 0;
                AdvanceRowWithScroll();
            }
            _pendingWrap = false;
        }

        _activeGrid[CursorRow][CursorColumn] = new TerminalCell(
            ch,
            CurrentForeground,
            CurrentBackground,
            CurrentAttributes);

        if (CursorColumn < Columns - 1)
        {
            CursorColumn++;
        }
        else if (AutoWrapMode)
        {
            // Deferred wrap: cursor remains at last column until next printable char
            _pendingWrap = true;
        }
    }

    // ── C0 Control characters ───────────────────────────────────────────────────

    public void CarriageReturn()
    {
        CursorColumn = 0;
        _pendingWrap = false;
    }

    public void LineFeed()
    {
        _pendingWrap = false;
        AdvanceRowWithScroll();
    }

    public void NewLine()
    {
        CarriageReturn();
        LineFeed();
    }

    public void Backspace()
    {
        _pendingWrap = false;
        if (CursorColumn > 0)
            CursorColumn--;
    }

    public void HorizontalTab(int count = 1)
    {
        _pendingWrap = false;
        for (int i = 0; i < count; i++)
        {
            int nextCol = Columns - 1;
            for (int c = CursorColumn + 1; c < Columns; c++)
            {
                if (_tabStops.Contains(c))
                {
                    nextCol = c;
                    break;
                }
            }
            CursorColumn = nextCol;
        }
    }

    public void BackwardTab(int count = 1)
    {
        _pendingWrap = false;
        for (int i = 0; i < count; i++)
        {
            int prevCol = 0;
            for (int c = CursorColumn - 1; c >= 0; c--)
            {
                if (_tabStops.Contains(c))
                {
                    prevCol = c;
                    break;
                }
            }
            CursorColumn = prevCol;
        }
    }

    /// <summary>Reverse Index (<c>ESC M</c>): move cursor up 1 row, scrolling down if at <see cref="ScrollTop"/>.</summary>
    public void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorRow == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    private void AdvanceRowWithScroll()
    {
        if (CursorRow == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    // ── Cursor movement (CSI A/B/C/D/E/F/G/H/f/d) ──────────────────────────────

    public void MoveCursorUp(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Max(1, count);
        int minRow = CursorRow >= ScrollTop ? ScrollTop : 0;
        CursorRow = Math.Max(minRow, CursorRow - n);
    }

    public void MoveCursorDown(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Max(1, count);
        int maxRow = CursorRow <= ScrollBottom ? ScrollBottom : Rows - 1;
        CursorRow = Math.Min(maxRow, CursorRow + n);
    }

    public void MoveCursorForward(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Max(1, count);
        CursorColumn = Math.Min(Columns - 1, CursorColumn + n);
    }

    public void MoveCursorBackward(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Max(1, count);
        CursorColumn = Math.Max(0, CursorColumn - n);
    }

    /// <summary>
    /// Sets the 0-based cursor position (<c>CSI row;col H</c> after 1-based conversion).
    /// Respects <see cref="OriginMode"/> (<c>DECOM</c>).
    /// </summary>
    public void SetCursorPosition(int row, int column)
    {
        _pendingWrap = false;

        int targetRow = OriginMode ? ScrollTop + row : row;
        int minRow    = OriginMode ? ScrollTop : 0;
        int maxRow    = OriginMode ? ScrollBottom : Rows - 1;

        CursorRow    = Math.Clamp(targetRow, minRow, maxRow);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    public void SetCursorColumn(int column)
    {
        _pendingWrap = false;
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    public void SetCursorRow(int row)
    {
        SetCursorPosition(row, CursorColumn);
    }

    // ── Scrolling & Scroll Region (DECSTBM: CSI top;bottom r) ───────────────────

    /// <summary>
    /// Sets the top and bottom margins of the scrolling region (0-based inclusive).
    /// Resets the cursor to home position.
    /// </summary>
    public void SetScrollRegion(int topRow, int bottomRow)
    {
        int top    = Math.Clamp(topRow, 0, Rows - 1);
        int bottom = Math.Clamp(bottomRow, 0, Rows - 1);

        if (top < bottom)
        {
            ScrollTop    = top;
            ScrollBottom = bottom;
        }
        else
        {
            ScrollTop    = 0;
            ScrollBottom = Rows - 1;
        }

        SetCursorPosition(0, 0);
    }

    public void ResetScrollRegion()
    {
        ScrollTop    = 0;
        ScrollBottom = Rows - 1;
        SetCursorPosition(0, 0);
    }

    /// <summary>
    /// Scrolls content within <c>[ScrollTop..ScrollBottom]</c> up by <paramref name="lines"/> rows.
    /// When on the primary screen and <see cref="ScrollTop"/> is 0, scrolled-off lines are pushed to scrollback.
    /// </summary>
    public void ScrollUp(int lines = 1)
    {
        int count = Math.Clamp(lines, 1, ScrollBottom - ScrollTop + 1);

        for (int i = 0; i < count; i++)
        {
            var topLine = _activeGrid[ScrollTop];

            if (!IsAlternateScreen && ScrollTop == 0 && _maxScrollbackLines > 0)
            {
                var copy = new TerminalCell[Columns];
                Array.Copy(topLine, copy, Columns);
                _scrollback.Add(copy);
                TrimScrollback();
            }

            for (int r = ScrollTop; r < ScrollBottom; r++)
            {
                _activeGrid[r] = _activeGrid[r + 1];
            }

            _activeGrid[ScrollBottom] = CreateBlankRow(Columns, CurrentForeground, CurrentBackground);
        }
    }

    /// <summary>
    /// Scrolls content within <c>[ScrollTop..ScrollBottom]</c> down by <paramref name="lines"/> rows.
    /// </summary>
    public void ScrollDown(int lines = 1)
    {
        int count = Math.Clamp(lines, 1, ScrollBottom - ScrollTop + 1);

        for (int i = 0; i < count; i++)
        {
            for (int r = ScrollBottom; r > ScrollTop; r--)
            {
                _activeGrid[r] = _activeGrid[r - 1];
            }

            _activeGrid[ScrollTop] = CreateBlankRow(Columns, CurrentForeground, CurrentBackground);
        }
    }

    private void TrimScrollback()
    {
        if (_maxScrollbackLines <= 0)
        {
            _scrollback.Clear();
            return;
        }

        int excess = _scrollback.Count - _maxScrollbackLines;
        if (excess > 0)
        {
            _scrollback.RemoveRange(0, excess);
        }
    }

    // ── Erase operations (CSI J, CSI K, CSI X) ──────────────────────────────────

    /// <summary>
    /// Erase in Display (<c>CSI Ps J</c>):
    /// 0 = cursor to end, 1 = start to cursor, 2 = entire display, 3 = entire display + scrollback.
    /// </summary>
    public void EraseInDisplay(int mode = 0)
    {
        _pendingWrap = false;
        var blank = CurrentBlankCell;

        switch (mode)
        {
            case 0: // Cursor to end of screen
                EraseRowRange(CursorRow, CursorColumn, Columns - 1, blank);
                for (int r = CursorRow + 1; r < Rows; r++)
                    Array.Fill(_activeGrid[r], blank);
                break;

            case 1: // Start of screen to cursor
                for (int r = 0; r < CursorRow; r++)
                    Array.Fill(_activeGrid[r], blank);
                EraseRowRange(CursorRow, 0, CursorColumn, blank);
                break;

            case 2: // Entire screen
                for (int r = 0; r < Rows; r++)
                    Array.Fill(_activeGrid[r], blank);
                break;

            case 3: // Entire screen + scrollback
                for (int r = 0; r < Rows; r++)
                    Array.Fill(_activeGrid[r], blank);
                _scrollback.Clear();
                break;
        }
    }

    /// <summary>
    /// Erase in Line (<c>CSI Ps K</c>):
    /// 0 = cursor to end of line, 1 = start of line to cursor, 2 = entire line.
    /// </summary>
    public void EraseInLine(int mode = 0)
    {
        _pendingWrap = false;
        var blank = CurrentBlankCell;

        switch (mode)
        {
            case 0:
                EraseRowRange(CursorRow, CursorColumn, Columns - 1, blank);
                break;
            case 1:
                EraseRowRange(CursorRow, 0, CursorColumn, blank);
                break;
            case 2:
                EraseRowRange(CursorRow, 0, Columns - 1, blank);
                break;
        }
    }

    /// <summary>Erase <paramref name="count"/> characters starting at the cursor (<c>CSI Ps X</c>).</summary>
    public void EraseCharacters(int count = 1)
    {
        _pendingWrap = false;
        int endCol = Math.Min(Columns - 1, CursorColumn + Math.Max(1, count) - 1);
        EraseRowRange(CursorRow, CursorColumn, endCol, CurrentBlankCell);
    }

    private void EraseRowRange(int row, int startCol, int endCol, TerminalCell blank)
    {
        var line = _activeGrid[row];
        for (int c = startCol; c <= endCol && c < Columns; c++)
            line[c] = blank;
    }

    // ── Insert / Delete Lines and Characters (CSI L, M, @, P) ───────────────────

    public void InsertLines(int count = 1)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
            return;

        _pendingWrap = false;
        int n = Math.Clamp(count, 1, ScrollBottom - CursorRow + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = ScrollBottom; r > CursorRow; r--)
                _activeGrid[r] = _activeGrid[r - 1];
            _activeGrid[CursorRow] = CreateBlankRow(Columns, CurrentForeground, CurrentBackground);
        }
    }

    public void DeleteLines(int count = 1)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
            return;

        _pendingWrap = false;
        int n = Math.Clamp(count, 1, ScrollBottom - CursorRow + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = CursorRow; r < ScrollBottom; r++)
                _activeGrid[r] = _activeGrid[r + 1];
            _activeGrid[ScrollBottom] = CreateBlankRow(Columns, CurrentForeground, CurrentBackground);
        }
    }

    public void InsertCharacters(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Clamp(count, 1, Columns - CursorColumn);
        var line = _activeGrid[CursorRow];

        for (int c = Columns - 1; c >= CursorColumn + n; c--)
            line[c] = line[c - n];

        var blank = CurrentBlankCell;
        for (int c = CursorColumn; c < CursorColumn + n; c++)
            line[c] = blank;
    }

    public void DeleteCharacters(int count = 1)
    {
        _pendingWrap = false;
        int n = Math.Clamp(count, 1, Columns - CursorColumn);
        var line = _activeGrid[CursorRow];

        for (int c = CursorColumn; c < Columns - n; c++)
            line[c] = line[c + n];

        var blank = CurrentBlankCell;
        for (int c = Columns - n; c < Columns; c++)
            line[c] = blank;
    }

    // ── Alternate screen buffer (CSI ?1049h / ?1049l, ?47h / ?47l) ──────────────

    public void SwitchToAlternateScreen(bool saveCursorAndClear = true)
    {
        if (IsAlternateScreen)
            return;

        if (saveCursorAndClear)
            SaveCursor();

        IsAlternateScreen = true;
        _activeGrid = _alternateGrid;

        if (saveCursorAndClear)
        {
            var blank = TerminalCell.Blank;
            for (int r = 0; r < Rows; r++)
                Array.Fill(_alternateGrid[r], blank);
            SetCursorPosition(0, 0);
        }
    }

    public void SwitchToPrimaryScreen(bool restoreCursor = true)
    {
        if (!IsAlternateScreen)
            return;

        IsAlternateScreen = false;
        _activeGrid = _primaryGrid;
        ScrollTop    = 0;
        ScrollBottom = Rows - 1;

        if (restoreCursor)
            RestoreCursor();
    }

    // ── Save / Restore Cursor (DECSC / DECRC) ───────────────────────────────────

    public void SaveCursor()
    {
        var state = new SavedCursorState(
            CursorRow,
            CursorColumn,
            CurrentForeground,
            CurrentBackground,
            CurrentAttributes,
            OriginMode,
            AutoWrapMode);

        if (IsAlternateScreen)
            _alternateSavedCursor = state;
        else
            _primarySavedCursor = state;
    }

    public void RestoreCursor()
    {
        var state = IsAlternateScreen ? _alternateSavedCursor : _primarySavedCursor;
        OriginMode        = state.OriginMode;
        AutoWrapMode      = state.AutoWrapMode || state == default;
        CurrentForeground = state == default ? TerminalColor.Default : state.Foreground;
        CurrentBackground = state == default ? TerminalColor.Default : state.Background;
        CurrentAttributes = state.Attributes;
        CursorRow         = Math.Clamp(state.Row, 0, Rows - 1);
        CursorColumn      = Math.Clamp(state.Col, 0, Columns - 1);
        _pendingWrap      = false;
    }

    // ── Tab Stops (ESC H, CSI g) ────────────────────────────────────────────────

    public void SetTabStopAtCursor() => _tabStops.Add(CursorColumn);

    public void ClearTabStop(int mode = 0)
    {
        if (mode == 0)
            _tabStops.Remove(CursorColumn);
        else if (mode == 3)
            _tabStops.Clear();
    }

    public void ResetDefaultTabStops()
    {
        _tabStops.Clear();
        for (int c = DefaultTabWidth; c < Columns; c += DefaultTabWidth)
            _tabStops.Add(c);
    }

    // ── Attributes & Full Reset ─────────────────────────────────────────────────

    public void ResetAttributes()
    {
        CurrentForeground = TerminalColor.Default;
        CurrentBackground = TerminalColor.Default;
        CurrentAttributes = CellAttributes.None;
    }

    public void FullReset()
    {
        ResetAttributes();
        IsAlternateScreen      = false;
        _primaryGrid           = CreateGrid(Rows, Columns);
        _alternateGrid         = CreateGrid(Rows, Columns);
        _activeGrid            = _primaryGrid;
        ScrollTop              = 0;
        ScrollBottom           = Rows - 1;
        CursorRow              = 0;
        CursorColumn           = 0;
        CursorVisible          = true;
        ApplicationCursorKeys  = false;
        AutoWrapMode           = true;
        OriginMode             = false;
        BracketedPasteMode     = false;
        _pendingWrap           = false;
        ResetDefaultTabStops();
    }

    // ── Resize ──────────────────────────────────────────────────────────────────

    public void Resize(int newColumns, int newRows)
    {
        newColumns = Math.Max(2, newColumns);
        newRows    = Math.Max(2, newRows);

        if (newColumns == Columns && newRows == Rows)
            return;

        _primaryGrid   = ResizeGrid(_primaryGrid, Rows, Columns, newRows, newColumns);
        _alternateGrid = ResizeGrid(_alternateGrid, Rows, Columns, newRows, newColumns);
        _activeGrid    = IsAlternateScreen ? _alternateGrid : _primaryGrid;

        bool wasFullRegion = ScrollTop == 0 && ScrollBottom == Rows - 1;
        Columns = newColumns;
        Rows    = newRows;

        if (wasFullRegion)
        {
            ScrollTop    = 0;
            ScrollBottom = Rows - 1;
        }
        else
        {
            ScrollTop    = Math.Clamp(ScrollTop, 0, Rows - 1);
            ScrollBottom = Math.Clamp(ScrollBottom, Math.Min(ScrollTop + 1, Rows - 1), Rows - 1);
        }

        CursorRow    = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, Columns - 1);
        _pendingWrap = false;

        for (int c = DefaultTabWidth; c < Columns; c += DefaultTabWidth)
        {
            if (c >= Columns) break;
            _tabStops.Add(c);
        }
    }

    private static TerminalCell[][] ResizeGrid(
        TerminalCell[][] oldGrid,
        int oldRows,
        int oldCols,
        int newRows,
        int newCols)
    {
        var newGrid = CreateGrid(newRows, newCols);
        int copyRows = Math.Min(oldRows, newRows);
        int copyCols = Math.Min(oldCols, newCols);

        for (int r = 0; r < copyRows; r++)
        {
            Array.Copy(oldGrid[r], 0, newGrid[r], 0, copyCols);
        }

        return newGrid;
    }

    // ── Inspection & Text Extraction ────────────────────────────────────────────

    public TerminalCell GetCell(int row, int column)
    {
        if ((uint)row >= (uint)Rows || (uint)column >= (uint)Columns)
            return TerminalCell.Blank;
        return _activeGrid[row][column];
    }

    /// <summary>
    /// Returns the row of cells for the given viewport row when scrolled back by
    /// <paramref name="scrollOffset"/> lines (0 = live screen).
    /// </summary>
    public ReadOnlySpan<TerminalCell> GetViewportRow(int viewportRow, int scrollOffset = 0)
    {
        if ((uint)viewportRow >= (uint)Rows)
            return ReadOnlySpan<TerminalCell>.Empty;

        if (IsAlternateScreen || scrollOffset <= 0 || _scrollback.Count == 0)
            return _activeGrid[viewportRow];

        int clampedOffset = Math.Min(scrollOffset, _scrollback.Count);
        int logicalIndex  = (_scrollback.Count - clampedOffset) + viewportRow;

        if (logicalIndex < _scrollback.Count)
            return _scrollback[logicalIndex];

        int screenRow = logicalIndex - _scrollback.Count;
        return (uint)screenRow < (uint)Rows
            ? _activeGrid[screenRow]
            : ReadOnlySpan<TerminalCell>.Empty;
    }

    /// <summary>
    /// Returns the trimmed text of a screen row on the active grid.
    /// </summary>
    public string GetLineText(int row, bool trimTrailingSpaces = true)
    {
        if ((uint)row >= (uint)Rows)
            return string.Empty;

        var cells = _activeGrid[row];
        var sb = new StringBuilder(Columns);
        for (int c = 0; c < Columns; c++)
        {
            char ch = cells[c].Character;
            sb.Append(ch == '\0' ? ' ' : ch);
        }

        return trimTrailingSpaces ? sb.ToString().TrimEnd(' ') : sb.ToString();
    }

    /// <summary>
    /// Extracts text between two coordinates in the current viewport (adjusted by <paramref name="scrollOffset"/>).
    /// </summary>
    public string ExtractViewportText(
        int startRow,
        int startCol,
        int endRow,
        int endCol,
        int scrollOffset = 0)
    {
        if (startRow > endRow || (startRow == endRow && startCol > endCol))
        {
            (startRow, endRow) = (endRow, startRow);
            (startCol, endCol) = (endCol, startCol);
        }

        startRow = Math.Clamp(startRow, 0, Rows - 1);
        endRow   = Math.Clamp(endRow, 0, Rows - 1);
        startCol = Math.Clamp(startCol, 0, Columns - 1);
        endCol   = Math.Clamp(endCol, 0, Columns - 1);

        var sb = new StringBuilder();
        for (int r = startRow; r <= endRow; r++)
        {
            var rowSpan = GetViewportRow(r, scrollOffset);
            int cStart  = r == startRow ? startCol : 0;
            int cEnd    = r == endRow ? endCol : Columns - 1;

            var lineBuilder = new StringBuilder();
            for (int c = cStart; c <= cEnd && c < rowSpan.Length; c++)
            {
                char ch = rowSpan[c].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }

            if (r < endRow)
            {
                sb.AppendLine(lineBuilder.ToString().TrimEnd(' '));
            }
            else
            {
                sb.Append(lineBuilder.ToString().TrimEnd(' '));
            }
        }

        return sb.ToString();
    }
}

using System;
using System.Runtime.InteropServices;

internal static class Program
{
    // ---- state ------------------------------------------------------------
    static IntPtr _hwnd;
    static long _freq, _start, _accum;
    static bool _running, _topmost;
    static int _lastSecond = -1;
    static bool _editing, _caretOn;
    static string _editBuf = "";
    static readonly long[] _laps = new long[512];
    static int _lapCount;
    static int _dpi = 96;
    static IntPtr _memDc, _memBmp, _memOld;
    static int _bufW, _bufH;

    static readonly RECT[] _btn = new RECT[4];
    static RECT _rcClock, _rcMain, _rcName, _rcLaps, _rcSwatch;
    static int _mainScale, _clockScale, _lapScale, _nameScale, _btnScale, _lapRowH;

    // palette, 0xRRGGBB
    const uint Bg = 0x0E1014, Surface = 0x171A20, Border = 0x2A2F38,
        Ink = 0xF2F3F5, Dim = 0x7C8494, Split = 0xB8C0CC, OnAccent = 0x140A26;

    // accent themes, cycled by the swatch above the controls
    static readonly uint[] _accentSet = { 0xB18CFF, 0x4ADE80, 0x7FB8FF, 0xFF8CC6, 0xFFC46B };
    static readonly uint[] _accentDimSet = { 0x7A63B8, 0x3B9E5F, 0x557FAF, 0xAC5F86, 0xAB8348 };
    static int _theme;
    static uint _accent = _accentSet[0], _accentDim = _accentDimSet[0];

    // ---- win32 constants --------------------------------------------------
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
    const int WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005,
        WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_ERASEBKGND = 0x0014,
        WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201, WM_GETMINMAXINFO = 0x0024;
    const uint SRCCOPY = 0x00CC0020;
    const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);

    // ---- entry ------------------------------------------------------------
    [STAThread]
    static void Main()
    {
        SetProcessDPIAware();
        IntPtr screen = GetDC(IntPtr.Zero);
        _dpi = GetDeviceCaps(screen, 90); // LOGPIXELSY
        ReleaseDC(IntPtr.Zero, screen);
        QueryPerformanceFrequency(out _freq);
        LoadState();

        IntPtr inst = GetModuleHandle(null);
        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc);
        wc.hInstance = inst;
        wc.hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        wc.hIconSm = wc.hIcon;
        wc.hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512));
        wc.lpszClassName = "PixelStopwatch";
        RegisterClassEx(ref wc);

        int cw = 420 * _dpi / 96, ch = 500 * _dpi / 96;
        _hwnd = CreateWindowEx(0, "PixelStopwatch", "Stopwatch", WS_OVERLAPPEDWINDOW,
            unchecked((int)0x80000000), unchecked((int)0x80000000), cw, ch,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        DarkTitleBar(_hwnd);
        ShowWindow(_hwnd, 1);
        UpdateWindow(_hwnd);

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    static readonly WndProcDelegate _proc = WndProc;

    // ---- window proc ------------------------------------------------------
    static IntPtr WndProc(IntPtr h, uint m, IntPtr wp, IntPtr lp)
    {
        switch (m)
        {
            case WM_CREATE:
                _hwnd = h;
                SetTimer(h, IntPtr.Zero, 250, IntPtr.Zero);
                return IntPtr.Zero;

            case WM_SIZE:
                Layout();
                InvalidateRect(h, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
                MINMAXINFO mm = (MINMAXINFO)Marshal.PtrToStructure(lp, typeof(MINMAXINFO));
                mm.ptMinTrackSize.x = 360 * _dpi / 96;
                mm.ptMinTrackSize.y = 280 * _dpi / 96;
                Marshal.StructureToPtr(mm, lp, false);
                return IntPtr.Zero;

            case WM_TIMER:
                Tick();
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                Paint(h);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                Click((short)(lp.ToInt64() & 0xFFFF), (short)((lp.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((lp.ToInt64() & (1L << 30)) == 0) Key((int)wp);
                return IntPtr.Zero;

            case WM_DESTROY:
                SaveState();
                ReleaseBuffer();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(h, m, wp, lp);
    }

    // ---- clock / stopwatch ------------------------------------------------
    static long Now() { long t; QueryPerformanceCounter(out t); return t; }

    static long ElapsedMs()
    {
        long t = _accum + (_running ? Now() - _start : 0);
        long sec = t / _freq;
        return sec * 1000 + (t % _freq) * 1000 / _freq;
    }

    static string Fmt(long ms)
    {
        // shortcut: minutes accumulate (no hours field); 4-digit minutes would clip at tiny widths
        return string.Format("{0:00}:{1:00}.{2:000}", ms / 60000, ms / 1000 % 60, ms % 1000);
    }

    static void Key(int k)
    {
        if (k == 0x20 || k == 0x0D) ToggleRun(); // space or enter; applies an edit first
        else if (_editing) EditKey(k);
        else if (k == 'R') Reset();
        else if (k == 'L') Lap();
        else if (k == 'T') ToggleTop();
        else if (k == 'E') BeginEdit();
    }

    static void Click(int x, int y)
    {
        if (x >= _rcSwatch.left && x < _rcSwatch.right && y >= _rcSwatch.top && y < _rcSwatch.bottom)
        {
            CycleTheme();
            return;
        }
        for (int i = 0; i < 4; i++)
            if (x >= _btn[i].left && x < _btn[i].right && y >= _btn[i].top && y < _btn[i].bottom)
            {
                if (i == 0) ToggleRun();
                else if (i == 1) Reset();
                else if (i == 2) Lap();
                else ToggleTop();
                return;
            }
        if (x >= _rcMain.left && x < _rcMain.right && y >= _rcMain.top && y < _rcMain.bottom)
            BeginEdit();
    }

    // ---- editing the reading ----------------------------------------------
    static void BeginEdit()
    {
        if (_running || _editing) return; // only a paused watch can be set
        _editing = true;
        _editBuf = "";
        _caretOn = true;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void CancelEdit()
    {
        _editing = false;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void ApplyEdit()
    {
        _editing = false;
        _accum = EditMs() * _freq / 1000;
        _lapCount = 0; // the timeline changed, old laps no longer line up
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // typed as seconds, optionally with up to 3 decimals: 45, 45.5, 0.045
    static long EditMs()
    {
        int dot = _editBuf.IndexOf('.');
        string whole = dot < 0 ? _editBuf : _editBuf.Substring(0, dot);
        string frac = dot < 0 ? "" : _editBuf.Substring(dot + 1);
        if (frac.Length > 3) frac = frac.Substring(0, 3);
        frac = frac.PadRight(3, '0');
        long w, f;
        long.TryParse(whole, out w);
        long.TryParse(frac, out f);
        long ms = w * 1000 + f;
        return ms > 5999999 ? 5999999 : ms; // cap at 99:59.999 so the caret fits
    }

    static void EditKey(int k)
    {
        _caretOn = true;
        if (k >= '0' && k <= '9') AppendDigit((char)k);
        else if (k >= 0x60 && k <= 0x69) AppendDigit((char)('0' + k - 0x60));
        else if (k == 0xBE || k == 0xBC || k == 0x6E) AppendDot();
        else if (k == 0x08) { if (_editBuf.Length > 0) _editBuf = _editBuf.Substring(0, _editBuf.Length - 1); }
        else if (k == 0x2E) _editBuf = "";
        else if (k == 0x1B) { CancelEdit(); return; }
        InvalidateRectRect(_hwnd, ref _rcMain, false);
    }

    static void AppendDigit(char c)
    {
        int dot = _editBuf.IndexOf('.');
        if (dot < 0) { if (_editBuf.Length >= 5) return; }
        else if (_editBuf.Length - dot - 1 >= 3) return;
        _editBuf += c;
    }

    static void AppendDot()
    {
        if (_editBuf.IndexOf('.') >= 0) return;
        if (_editBuf.Length == 0) _editBuf = "0";
        _editBuf += ".";
    }

    static void Tick()
    {
        SYSTEMTIME st;
        GetLocalTime(out st);
        int sec = st.wHour * 3600 + st.wMinute * 60 + st.wSecond;
        if (sec != _lastSecond)
        {
            _lastSecond = sec;
            InvalidateRect(_hwnd, IntPtr.Zero, false); // clock changed: full repaint
        }
        else if (_editing)
        {
            _caretOn = !_caretOn;
            InvalidateRectRect(_hwnd, ref _rcMain, false);
        }
        else if (_running)
        {
            InvalidateRectRect(_hwnd, ref _rcMain, false); // digits only
        }
    }

    static void ToggleRun()
    {
        if (_editing) ApplyEdit();
        if (_running)
        {
            _accum += Now() - _start;
            _running = false;
            SetTimer(_hwnd, IntPtr.Zero, 250, IntPtr.Zero);
        }
        else
        {
            _start = Now();
            _running = true;
            SetTimer(_hwnd, IntPtr.Zero, 15, IntPtr.Zero);
        }
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void Reset()
    {
        _editing = false;
        _running = false;
        _accum = 0;
        _lapCount = 0;
        SetTimer(_hwnd, IntPtr.Zero, 250, IntPtr.Zero);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void Lap()
    {
        if (_editing) return;
        // shortcut: 512 lap cap keeps memory fixed; raise or ring-buffer if ever needed
        if (_lapCount < _laps.Length) _laps[_lapCount++] = ElapsedMs();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void ToggleTop()
    {
        _topmost = !_topmost;
        SetWindowPos(_hwnd, _topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void CycleTheme()
    {
        _theme = (_theme + 1) % _accentSet.Length;
        _accent = _accentSet[_theme];
        _accentDim = _accentDimSet[_theme];
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // ---- state persistence -------------------------------------------------
    // The last reading survives a close; it reopens paused at that time.
    static void SaveState()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\PixelStopwatch"))
            {
                k.SetValue("ElapsedMs", ElapsedMs());
            }
        }
        catch { }
    }

    static void LoadState()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\PixelStopwatch"))
            {
                if (k == null) return;
                object v = k.GetValue("ElapsedMs");
                if (v == null) return;
                long ms = Convert.ToInt64(v);
                if (ms > 0) _accum = ms * _freq / 1000;
            }
        }
        catch { }
    }

    // ---- layout -----------------------------------------------------------
    static void Layout()
    {
        ReleaseBuffer();
        RECT rc;
        GetClientRect(_hwnd, out rc);
        int w = Math.Max(1, rc.right), h = Math.Max(1, rc.bottom);
        int pad = Clamp(h / 28, 8, 28);
        int innerW = Math.Max(1, w - 2 * pad);

        // 5x7 glyphs, 1px gap: 10 glyphs = 59 units, 7 units tall
        _mainScale = Math.Max(2, Math.Min(innerW / 59, h * 38 / 100 / 7));
        _clockScale = Clamp(_mainScale / 2, 1, 4);
        _lapScale = Clamp(Math.Min(_mainScale * 2 / 5, (innerW * 40 / 100 - 8) / 53), 1, 4);

        _rcClock = new RECT { left = pad, top = pad, right = w - pad, bottom = pad + 7 * _clockScale };

        // small colour swatch in the top-right corner of the clock row
        int sw = Clamp(8 * _clockScale, 14, 28);
        _rcSwatch = new RECT { left = w - pad - sw, top = pad, right = w - pad, bottom = pad + sw };

        int btnH = Clamp(h * 10 / 100, 30, 56);
        int btnTop = h - pad - btnH;
        int gap = Clamp(w / 80, 6, 12);
        int bw = Math.Max(20, (innerW - 3 * gap) / 4);
        _btnScale = Clamp(Math.Min((bw - 12) / 29, (btnH - 12) / 7), 1, 4);

        // centre the stopwatch between the clock and the buttons
        int regionTop = _rcClock.bottom + pad;
        int regionBottom = btnTop - pad;
        int mainTop = regionTop + Math.Max(0, (regionBottom - regionTop - 7 * _mainScale) / 2);
        _rcMain = new RECT { left = pad, top = mainTop, right = w - pad, bottom = mainTop + 7 * _mainScale };

        for (int i = 0; i < 4; i++)
            _btn[i] = new RECT
            {
                left = pad + i * (bw + gap),
                top = btnTop,
                right = pad + i * (bw + gap) + bw,
                bottom = btnTop + btnH
            };

        _rcLaps = new RECT { left = pad, top = _rcMain.bottom + pad, right = w - pad, bottom = regionBottom };
        _lapRowH = 7 * _lapScale + Clamp(_lapScale * 3, 6, 14);

        // name sits just above the controls at a fixed small size
        _nameScale = 2;
        int nameBottom = btnTop - pad / 2;
        _rcName = new RECT
        {
            left = pad,
            top = nameBottom - 7 * _nameScale,
            right = w - pad,
            bottom = nameBottom
        };
        _rcLaps.bottom = _rcName.top - pad / 2;
    }

    // ---- painting ---------------------------------------------------------
    static void Paint(IntPtr hwnd)
    {
        PAINTSTRUCT ps;
        IntPtr dc = BeginPaint(hwnd, out ps);
        RECT rc;
        GetClientRect(hwnd, out rc);
        int w = Math.Max(1, rc.right), h = Math.Max(1, rc.bottom);

        bool fresh = _memDc == IntPtr.Zero || _bufW != w || _bufH != h;
        EnsureBuffer(dc, w, h);
        RECT u = ps.rcPaint;
        bool partial = !fresh
            && u.left >= _rcMain.left && u.top >= _rcMain.top
            && u.right <= _rcMain.right && u.bottom <= _rcMain.bottom;

        if (partial)
        {
            Fill(_memDc, u, Bg);
            DrawTime(_memDc);
            BitBlt(dc, u.left, u.top, u.right - u.left, u.bottom - u.top,
                _memDc, u.left, u.top, SRCCOPY);
        }
        else
        {
            Fill(_memDc, new RECT { right = w, bottom = h }, Bg);
            DrawUi(_memDc);
            BitBlt(dc, 0, 0, w, h, _memDc, 0, 0, SRCCOPY);
        }
        EndPaint(hwnd, ref ps);
    }

    static void EnsureBuffer(IntPtr dc, int w, int h)
    {
        if (_memDc != IntPtr.Zero && w == _bufW && h == _bufH) return;
        ReleaseBuffer();
        _memDc = CreateCompatibleDC(dc);
        _memBmp = CreateCompatibleBitmap(dc, w, h);
        _memOld = SelectObject(_memDc, _memBmp);
        _bufW = w;
        _bufH = h;
    }

    static void ReleaseBuffer()
    {
        if (_memDc == IntPtr.Zero) return;
        SelectObject(_memDc, _memOld);
        DeleteObject(_memBmp);
        DeleteDC(_memDc);
        _memDc = _memBmp = _memOld = IntPtr.Zero;
        _bufW = _bufH = 0;
    }

    static void DrawUi(IntPtr dc)
    {
        DrawClock(dc);
        DrawSwatch(dc);
        DrawTime(dc);
        DrawButtons(dc);
        DrawLaps(dc);
        DrawName(dc);
    }

    static void DrawSwatch(IntPtr dc)
    {
        Box(dc, _rcSwatch, _accent, Border);
    }

    static void DrawName(IntPtr dc)
    {
        PixelCenter(dc, "DESIGNED BY YAKOOB", _rcName, _nameScale, _accentDim);
    }

    static void DrawClock(IntPtr dc)
    {
        SYSTEMTIME st;
        GetLocalTime(out st);
        PixelCenter(dc, string.Format("{0:00}:{1:00}:{2:00}", st.wHour, st.wMinute, st.wSecond),
            _rcClock, _clockScale, Dim);
    }

    static void DrawTime(IntPtr dc)
    {
        if (_editing)
        {
            string s = Fmt(EditMs());
            int scale = _mainScale;
            int tw = TextWidth(s, scale);
            int x = _rcMain.left + (_rcMain.right - _rcMain.left - tw) / 2;
            int y = _rcMain.top + (_rcMain.bottom - _rcMain.top - 7 * scale) / 2;
            PixelText(dc, s, x, y, scale, _accent);
            if (_caretOn)
                Fill(dc, new RECT
                {
                    left = x + tw + 2 * scale,
                    top = y,
                    right = x + tw + 4 * scale,
                    bottom = y + 7 * scale
                }, _accent);
            return;
        }
        PixelCenter(dc, Fmt(ElapsedMs()), _rcMain, _mainScale, _running ? _accent : _accentDim);
    }

    static void DrawButtons(IntPtr dc)
    {
        for (int i = 0; i < 4; i++)
        {
            bool filled = i == 0 || (i == 3 && _topmost);
            if (filled) Fill(dc, _btn[i], _accent);
            else Box(dc, _btn[i], Surface, Border);
            PixelCenter(dc, Label(i), _btn[i], _btnScale, filled ? OnAccent : Ink);
        }
    }

    static string Label(int i)
    {
        if (i == 0) return _running ? "PAUSE" : "START";
        if (i == 1) return "RESET";
        if (i == 2) return "LAP";
        return "PIN";
    }

    static void DrawLaps(IntPtr dc)
    {
        if (_lapCount == 0) return;
        int left = _rcLaps.left, right = _rcLaps.right;
        if (right <= left) return;
        int c1 = left + (right - left) * 20 / 100;
        int c2 = left + (right - left) * 60 / 100;

        int y = _rcLaps.top;
        int rows = (_rcLaps.bottom - y) / _lapRowH;
        if (rows < 2) return;

        PixelText(dc, "LAP", left, y + RowTextOffset(), _lapScale, Dim);
        PixelText(dc, "SPLIT", c2 - TextWidth("SPLIT", _lapScale), y + RowTextOffset(), _lapScale, Dim);
        PixelText(dc, "TOTAL", right - TextWidth("TOTAL", _lapScale), y + RowTextOffset(), _lapScale, Dim);
        y += _lapRowH;

        for (int k = 0; k < rows - 1 && k < _lapCount; k++)
        {
            int i = _lapCount - 1 - k;
            long total = _laps[i];
            long split = total - (i > 0 ? _laps[i - 1] : 0);

            PixelText(dc, (i + 1).ToString("00"), left, y + RowTextOffset(), _lapScale, Dim);
            PixelText(dc, Fmt(split), c2 - TextWidth(Fmt(split), _lapScale),
                y + RowTextOffset(), _lapScale, Split);
            PixelText(dc, Fmt(total), right - TextWidth(Fmt(total), _lapScale),
                y + RowTextOffset(), _lapScale, Ink);
            y += _lapRowH;
        }
    }

    static int RowTextOffset()
    {
        return (_lapRowH - 7 * _lapScale) / 2;
    }

    // ---- pixel font -------------------------------------------------------
    // 5x7 glyphs, rows top to bottom, indexed by Glyph()
    static readonly string[] _glyphs =
    {
        /*A*/ "01110"+"10001"+"10001"+"11111"+"10001"+"10001"+"10001",
        /*B*/ "11110"+"10001"+"10001"+"11110"+"10001"+"10001"+"11110",
        /*C*/ "01110"+"10001"+"10000"+"10000"+"10000"+"10001"+"01110",
        /*D*/ "11110"+"10001"+"10001"+"10001"+"10001"+"10001"+"11110",
        /*E*/ "11111"+"10000"+"10000"+"11110"+"10000"+"10000"+"11111",
        /*F*/ "11111"+"10000"+"10000"+"11110"+"10000"+"10000"+"10000",
        /*G*/ "01110"+"10001"+"10000"+"10111"+"10001"+"10001"+"01111",
        /*H*/ "10001"+"10001"+"10001"+"11111"+"10001"+"10001"+"10001",
        /*I*/ "01110"+"00100"+"00100"+"00100"+"00100"+"00100"+"01110",
        /*J*/ "00111"+"00010"+"00010"+"00010"+"00010"+"10010"+"01100",
        /*K*/ "10001"+"10010"+"10100"+"11000"+"10100"+"10010"+"10001",
        /*L*/ "10000"+"10000"+"10000"+"10000"+"10000"+"10000"+"11111",
        /*M*/ "10001"+"11011"+"10101"+"10101"+"10001"+"10001"+"10001",
        /*N*/ "10001"+"11001"+"10101"+"10011"+"10001"+"10001"+"10001",
        /*O*/ "01110"+"10001"+"10001"+"10001"+"10001"+"10001"+"01110",
        /*P*/ "11110"+"10001"+"10001"+"11110"+"10000"+"10000"+"10000",
        /*Q*/ "01110"+"10001"+"10001"+"10001"+"10101"+"10010"+"01101",
        /*R*/ "11110"+"10001"+"10001"+"11110"+"10100"+"10010"+"10001",
        /*S*/ "01111"+"10000"+"10000"+"01110"+"00001"+"00001"+"11110",
        /*T*/ "11111"+"00100"+"00100"+"00100"+"00100"+"00100"+"00100",
        /*U*/ "10001"+"10001"+"10001"+"10001"+"10001"+"10001"+"01110",
        /*V*/ "10001"+"10001"+"10001"+"10001"+"10001"+"01010"+"00100",
        /*W*/ "10001"+"10001"+"10001"+"10101"+"10101"+"11011"+"10001",
        /*X*/ "10001"+"10001"+"01010"+"00100"+"01010"+"10001"+"10001",
        /*Y*/ "10001"+"10001"+"01010"+"00100"+"00100"+"00100"+"00100",
        /*Z*/ "11111"+"00001"+"00010"+"00100"+"01000"+"10000"+"11111",
        /*0*/ "01110"+"10001"+"10011"+"10101"+"11001"+"10001"+"01110",
        /*1*/ "00100"+"01100"+"00100"+"00100"+"00100"+"00100"+"01110",
        /*2*/ "01110"+"10001"+"00001"+"00010"+"00100"+"01000"+"11111",
        /*3*/ "11111"+"00010"+"00100"+"00010"+"00001"+"10001"+"01110",
        /*4*/ "00010"+"00110"+"01010"+"10010"+"11111"+"00010"+"00010",
        /*5*/ "11111"+"10000"+"11110"+"00001"+"00001"+"10001"+"01110",
        /*6*/ "00110"+"01000"+"10000"+"11110"+"10001"+"10001"+"01110",
        /*7*/ "11111"+"00001"+"00010"+"00100"+"01000"+"01000"+"01000",
        /*8*/ "01110"+"10001"+"10001"+"01110"+"10001"+"10001"+"01110",
        /*9*/ "01110"+"10001"+"10001"+"01111"+"00001"+"00010"+"01100",
        /*:*/ "00000"+"00110"+"00110"+"00000"+"00110"+"00110"+"00000",
        /*.*/ "00000"+"00000"+"00000"+"00000"+"00000"+"00110"+"00110",
        /*-*/ "00000"+"00000"+"00000"+"11111"+"00000"+"00000"+"00000",
        /* */ "00000"+"00000"+"00000"+"00000"+"00000"+"00000"+"00000",
    };

    static int Glyph(char c)
    {
        if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A'); // font is caps only
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= '0' && c <= '9') return 26 + c - '0';
        if (c == ':') return 36;
        if (c == '.') return 37;
        if (c == '-') return 38;
        return 39; // space, and anything unexpected
    }

    static int TextWidth(string s, int scale)
    {
        return s.Length * 6 * scale - scale;
    }

    static void PixelText(IntPtr dc, string s, int x, int y, int scale, uint rgb)
    {
        IntPtr br = CreateSolidBrush(Cr(rgb));
        int adv = 6 * scale;
        for (int i = 0; i < s.Length; i++)
        {
            string g = _glyphs[Glyph(s[i])];
            for (int row = 0; row < 7; row++)
            {
                int run = -1;
                for (int col = 0; col <= 5; col++)
                {
                    bool on = col < 5 && g[row * 5 + col] == '1';
                    if (on && run < 0) run = col;
                    else if (!on && run >= 0)
                    {
                        RECT r = new RECT
                        {
                            left = x + run * scale,
                            top = y + row * scale,
                            right = x + col * scale,
                            bottom = y + (row + 1) * scale
                        };
                        FillRect(dc, ref r, br);
                        run = -1;
                    }
                }
            }
            x += adv;
        }
        DeleteObject(br);
    }

    static void PixelCenter(IntPtr dc, string s, RECT r, int scale, uint rgb)
    {
        PixelText(dc, s,
            r.left + (r.right - r.left - TextWidth(s, scale)) / 2,
            r.top + (r.bottom - r.top - 7 * scale) / 2,
            scale, rgb);
    }

    // ---- gdi helpers ------------------------------------------------------
    static void Box(IntPtr dc, RECT r, uint fill, uint edge)
    {
        Fill(dc, r, edge);
        Fill(dc, new RECT
        {
            left = r.left + 2,
            top = r.top + 2,
            right = r.right - 2,
            bottom = r.bottom - 2
        }, fill);
    }

    static void Fill(IntPtr dc, RECT r, uint rgb)
    {
        IntPtr br = CreateSolidBrush(Cr(rgb));
        FillRect(dc, ref r, br);
        DeleteObject(br);
    }

    static uint Cr(uint rgb)
    {
        return ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);
    }

    static int Clamp(int v, int lo, int hi)
    {
        return v < lo ? lo : (v > hi ? hi : v);
    }

    static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        try
        {
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // ---- interop ----------------------------------------------------------
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern void QueryPerformanceCounter(out long t);
    [DllImport("kernel32.dll")] static extern void QueryPerformanceFrequency(out long f);
    [DllImport("kernel32.dll")] static extern void GetLocalTime(out SYSTEMTIME st);

    [DllImport("user32.dll")] static extern void SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr cb);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
    static extern bool InvalidateRectRect(IntPtr h, ref RECT r, bool erase);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint cr);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}

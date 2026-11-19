using System;
using System.Runtime.InteropServices;

internal static class Program
{
    // ---- state ------------------------------------------------------------
    static IntPtr _hwnd;
    static long _freq, _start, _accum;
    static bool _running, _topmost;
    static int _lastSecond = -1;
    static readonly long[] _laps = new long[512];
    static int _lapCount;
    static int _dpi = 96;
    static IntPtr _memDc, _memBmp, _memOld;
    static int _bufW, _bufH;

    static IntPtr _fClock, _fMain, _fBtn, _fLap;
    static readonly RECT[] _btn = new RECT[4];
    static RECT _rcClock, _rcMain, _rcLaps;
    static int _lapRowH;

    // palette, 0xRRGGBB
    const uint Bg = 0x14161A, Surface = 0x1B1E24, Border = 0x2C313B,
        Ink = 0xEEF1F5, Dim = 0x8892A0, Split = 0xC2CBD6,
        Accent = 0x58B6FF, OnAccent = 0x0A1017;

    // ---- win32 constants --------------------------------------------------
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
    const int WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005,
        WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_ERASEBKGND = 0x0014,
        WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201, WM_GETMINMAXINFO = 0x0024;
    const uint DT_CENTER = 1, DT_RIGHT = 2, DT_VCENTER = 4,
        DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;
    const int PS_SOLID = 0, FW_SEMIBOLD = 600, DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;
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

        IntPtr inst = GetModuleHandle(null);
        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc);
        wc.hInstance = inst;
        wc.hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        wc.hIconSm = wc.hIcon;
        wc.hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512));
        wc.lpszClassName = "MinStopwatch";
        RegisterClassEx(ref wc);

        int cw = 420 * _dpi / 96, ch = 500 * _dpi / 96;
        _hwnd = CreateWindowEx(0, "MinStopwatch", "Stopwatch", WS_OVERLAPPEDWINDOW,
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
                mm.ptMinTrackSize.x = 280 * _dpi / 96;
                mm.ptMinTrackSize.y = 260 * _dpi / 96;
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
                ReleaseBuffer();
                DeleteFonts();
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
        if (k == 0x20) ToggleRun();
        else if (k == 'R') Reset();
        else if (k == 'L') Lap();
        else if (k == 'T') ToggleTop();
    }

    static void Click(int x, int y)
    {
        for (int i = 0; i < 4; i++)
            if (x >= _btn[i].left && x < _btn[i].right && y >= _btn[i].top && y < _btn[i].bottom)
            {
                if (i == 0) ToggleRun();
                else if (i == 1) Reset();
                else if (i == 2) Lap();
                else ToggleTop();
                return;
            }
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
        else if (_running)
        {
            InvalidateRectRect(_hwnd, ref _rcMain, false); // digits only
        }
    }

    static void ToggleRun()
    {
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
        _running = false;
        _accum = 0;
        _lapCount = 0;
        SetTimer(_hwnd, IntPtr.Zero, 250, IntPtr.Zero);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    static void Lap()
    {
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

    // ---- layout -----------------------------------------------------------
    static void Layout()
    {
        ReleaseBuffer(); // release fonts selected into the back buffer before deleting them
        RECT rc;
        GetClientRect(_hwnd, out rc);
        int w = Math.Max(1, rc.right), h = Math.Max(1, rc.bottom);

        int pad = Clamp(h / 26, 8, 30);
        int clockH = Clamp(h * 7 / 100, 12, 32);
        int btnH = Clamp(h * 13 / 100, 26, 64);
        int gap = Clamp(w / 70, 5, 14);

        _rcClock = new RECT { left = pad, top = pad, right = w - pad, bottom = pad + clockH };
        int top = _rcClock.bottom + pad / 2;
        int btnTop = h - pad - btnH;
        int bottom = btnTop - pad / 2;
        int avail = Math.Max(24, bottom - top);

        int mainH = Math.Max(14, Math.Min((w - 2 * pad) * 18 / 100, avail * 62 / 100));
        int block = mainH * 13 / 10;
        _rcMain = new RECT { left = pad, top = top, right = w - pad, bottom = top + block };
        _rcLaps = new RECT { left = pad, top = _rcMain.bottom + pad / 3, right = w - pad, bottom = bottom };

        int inner = w - 2 * pad;
        int bw = Math.Max(24, (inner - 3 * gap) / 4);
        for (int i = 0; i < 4; i++)
            _btn[i] = new RECT
            {
                left = pad + i * (bw + gap),
                top = btnTop,
                right = pad + i * (bw + gap) + bw,
                bottom = btnTop + btnH
            };

        int lapH = Clamp(mainH * 17 / 100, 11, 18);
        _lapRowH = lapH * 2;
        DeleteFonts();
        _fMain = MakeFont(mainH, 400, "Consolas");
        _fClock = MakeFont(Clamp(mainH * 24 / 100, 12, 30), 400, "Consolas");
        _fBtn = MakeFont(Clamp(Math.Min(btnH * 42 / 100, bw * 26 / 100), 11, 20), FW_SEMIBOLD, "Segoe UI");
        _fLap = MakeFont(lapH, 400, "Consolas");
    }

    static IntPtr MakeFont(int px, int weight, string face)
    {
        return CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, face);
    }

    static void DeleteFonts()
    {
        if (_fMain != IntPtr.Zero) DeleteObject(_fMain);
        if (_fClock != IntPtr.Zero) DeleteObject(_fClock);
        if (_fBtn != IntPtr.Zero) DeleteObject(_fBtn);
        if (_fLap != IntPtr.Zero) DeleteObject(_fLap);
        _fMain = _fClock = _fBtn = _fLap = IntPtr.Zero;
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
        bool partial = !fresh && _fMain != IntPtr.Zero
            && u.left >= _rcMain.left && u.top >= _rcMain.top
            && u.right <= _rcMain.right && u.bottom <= _rcMain.bottom;

        if (partial)
        {
            Fill(_memDc, u, Bg);
            SetBkMode(_memDc, 1);
            DrawTime(_memDc);
            BitBlt(dc, u.left, u.top, u.right - u.left, u.bottom - u.top,
                _memDc, u.left, u.top, SRCCOPY);
        }
        else
        {
            Fill(_memDc, new RECT { right = w, bottom = h }, Bg);
            if (_fMain != IntPtr.Zero) DrawUi(_memDc);
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
        SetBkMode(dc, 1);
        DrawClock(dc);
        DrawTime(dc);
        DrawButtons(dc);
        DrawLaps(dc);
    }

    static void DrawClock(IntPtr dc)
    {
        SYSTEMTIME st;
        GetLocalTime(out st);
        RECT r = _rcClock;
        Text(dc, string.Format("{0:00}:{1:00}:{2:00}", st.wHour, st.wMinute, st.wSecond),
            _fClock, Dim, DT_CENTER | DT_VCENTER | DT_SINGLELINE, ref r);
    }

    static void DrawTime(IntPtr dc)
    {
        RECT r = _rcMain;
        Text(dc, Fmt(ElapsedMs()), _fMain, _running ? Ink : Dim,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE, ref r);
    }

    static void DrawButtons(IntPtr dc)
    {
        RECT r;
        for (int i = 0; i < 4; i++)
        {
            bool primary = i == 0;
            bool active = i == 3 && _topmost;
            bool filled = primary || active;
            int bh = _btn[i].bottom - _btn[i].top;
            RoundedBox(dc, _btn[i], filled ? Accent : Surface, filled ? Accent : Border,
                Clamp(bh * 35 / 100, 8, 20));
            r = _btn[i];
            Text(dc, Label(i), _fBtn, filled ? OnAccent : Ink,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE, ref r);
        }
    }

    static string Label(int i)
    {
        if (i == 0) return _running ? "PAUSE" : "START";
        if (i == 1) return "RESET";
        if (i == 2) return "LAP";
        return "ON TOP";
    }

    static void DrawLaps(IntPtr dc)
    {
        if (_lapCount == 0 || _lapRowH <= 0) return;
        int left = _rcLaps.left, right = _rcLaps.right;
        int c1 = left + (right - left) * 24 / 100;
        int c2 = left + (right - left) * 62 / 100;

        int y = _rcLaps.top;
        RECT a = new RECT { left = left, top = y, right = c1, bottom = y + _lapRowH };
        RECT b = new RECT { left = c1, top = y, right = c2, bottom = y + _lapRowH };
        RECT c = new RECT { left = c2, top = y, right = right, bottom = y + _lapRowH };
        Text(dc, "LAP", _fLap, Dim, DT_VCENTER | DT_SINGLELINE, ref a);
        Text(dc, "SPLIT", _fLap, Dim, DT_RIGHT | DT_VCENTER | DT_SINGLELINE, ref b);
        Text(dc, "TOTAL", _fLap, Dim, DT_RIGHT | DT_VCENTER | DT_SINGLELINE, ref c);
        y += _lapRowH;

        int rows = (_rcLaps.bottom - y) / _lapRowH;
        for (int k = 0; k < rows && k < _lapCount; k++)
        {
            int i = _lapCount - 1 - k;
            long total = _laps[i];
            long split = total - (i > 0 ? _laps[i - 1] : 0);

            a = new RECT { left = left, top = y, right = c1, bottom = y + _lapRowH };
            b = new RECT { left = c1, top = y, right = c2, bottom = y + _lapRowH };
            c = new RECT { left = c2, top = y, right = right, bottom = y + _lapRowH };
            Text(dc, (i + 1).ToString("00"), _fLap, Dim, DT_VCENTER | DT_SINGLELINE, ref a);
            Text(dc, Fmt(split), _fLap, Split, DT_RIGHT | DT_VCENTER | DT_SINGLELINE, ref b);
            Text(dc, Fmt(total), _fLap, Ink, DT_RIGHT | DT_VCENTER | DT_SINGLELINE, ref c);
            y += _lapRowH;
        }
    }

    // ---- gdi helpers ------------------------------------------------------
    static void Text(IntPtr dc, string s, IntPtr font, uint rgb, uint fmt, ref RECT r)
    {
        SelectObject(dc, font);
        SetTextColor(dc, Cr(rgb));
        DrawTextW(dc, s, -1, ref r, fmt | DT_NOPREFIX);
    }

    static void RoundedBox(IntPtr dc, RECT r, uint fill, uint edge, int radius)
    {
        IntPtr br = CreateSolidBrush(Cr(fill));
        IntPtr pn = CreatePen(PS_SOLID, 1, Cr(edge));
        IntPtr ob = SelectObject(dc, br);
        IntPtr op = SelectObject(dc, pn);
        RoundRect(dc, r.left, r.top, r.right, r.bottom, radius, radius);
        SelectObject(dc, ob);
        SelectObject(dc, op);
        DeleteObject(br);
        DeleteObject(pn);
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
    [DllImport("kernel32.dll")] static extern void GetLocalTime(out SYSTEMTIME st);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DrawTextW(IntPtr dc, string s, int len, ref RECT r, uint fmt);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, uint cr);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint cr);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, uint cr);
    [DllImport("gdi32.dll")] static extern bool RoundRect(IntPtr dc, int l, int t, int r, int b, int ew, int eh);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitch, string face);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}

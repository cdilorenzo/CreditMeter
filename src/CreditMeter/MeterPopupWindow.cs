using System.Globalization;
using System.Runtime.InteropServices;
using static CreditMeter.NativeMethods;

namespace CreditMeter;

/// <summary>
/// Tiny borderless, topmost "taxi meter" popup shown when the user
/// left-clicks the tray icon. Pure Win32/GDI — no WinForms/WPF/XAML.
/// It only ever reads <see cref="CreditState"/>; it never calls the
/// GitHub API itself.
/// </summary>
internal static class MeterPopupWindow
{
    private const string ClassName = "CreditMeterPopupWindowClass";
    private const int Width = 280;
    private const int Height = 135;

    // Keep a static reference so the GC never collects the delegate while
    // unmanaged code still holds a pointer to it.
    private static readonly WndProc s_wndProc = WindowProc;

    private static IntPtr s_hWnd;
    private static bool s_classRegistered;
    private static CreditState? s_state;

    /// <summary>Shows the popup near the cursor if hidden, hides it if visible.</summary>
    public static void Toggle(IntPtr hInstance, CreditState state)
    {
        s_state = state;
        EnsureCreated(hInstance);

        if (IsWindowVisible(s_hWnd))
        {
            Hide();
        }
        else
        {
            ShowNearCursor();
        }
    }

    /// <summary>Called by the polling loop after state changes; repaints only if currently shown.</summary>
    public static void NotifyStateChanged()
    {
        if (s_hWnd != IntPtr.Zero && IsWindowVisible(s_hWnd))
        {
            _ = InvalidateRect(s_hWnd, IntPtr.Zero, true);
        }
    }

    public static void Hide()
    {
        if (s_hWnd != IntPtr.Zero)
        {
            _ = ShowWindow(s_hWnd, SW_HIDE);
        }
    }

    private static void EnsureCreated(IntPtr hInstance)
    {
        if (!s_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                lpszMenuName = null,
                lpszClassName = ClassName,
                hIconSm = IntPtr.Zero
            };

            _ = RegisterClassEx(ref wc);
            s_classRegistered = true;
        }

        if (s_hWnd == IntPtr.Zero)
        {
            s_hWnd = CreateWindowEx(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                ClassName, "CreditMeter", WS_POPUP,
                0, 0, Width, Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
    }

    private static void ShowNearCursor()
    {
        _ = GetCursorPos(out POINT pt);

        // Anchor above-left of the cursor, similar to how the tray's own
        // flyouts appear near the notification area.
        int x = pt.X - Width + 20;
        int y = pt.Y - Height - 10;

        _ = SetWindowPos(s_hWnd, HWND_TOPMOST, x, y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _ = ShowWindow(s_hWnd, SW_SHOWNOACTIVATE);
        _ = SetForegroundWindow(s_hWnd);
        _ = SetFocus(s_hWnd);
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE)
                {
                    Hide();
                }
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                // Closing on focus loss is what makes it feel like a flyout
                // rather than a stray window left behind on the desktop.
                Hide();
                return IntPtr.Zero;

            case WM_DESTROY:
                // Owned by the tray app's lifetime — never post WM_QUIT here.
                return IntPtr.Zero;

            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void Paint(IntPtr hWnd)
    {
        IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);

        _ = GetClientRect(hWnd, out RECT rect);

        // Outer bezel + inset "screen" gives the little meter panel a boxed,
        // dashboard-instrument feel using nothing but a couple of FillRect calls.
        IntPtr bezel = CreateSolidBrush(MeterColors.Track); // dark gray bezel
        _ = FillRect(hdc, ref rect, bezel);
        _ = DeleteObject(bezel);

        var screen = new RECT { Left = rect.Left + 4, Top = rect.Top + 4, Right = rect.Right - 4, Bottom = rect.Bottom - 4 };
        IntPtr screenBrush = CreateSolidBrush(MeterColors.Glass);
        _ = FillRect(hdc, ref screen, screenBrush);
        _ = DeleteObject(screenBrush);

        _ = SetBkMode(hdc, TRANSPARENT_BKMODE);

        PopupContent content = DescribeState(s_state);

        IntPtr titleFont = CreateSegoeFont(-15, FW_BOLD);
        IntPtr valueFont = content.IsFare
            ? CreateFont(
                -40, 0, 0, 0, FW_BOLD,
                0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
                DEFAULT_PITCH | FF_SWISS, "Consolas") // monospace "digital" fare display
            : CreateSegoeFont(-20, FW_BOLD); // shorter state labels ("Loading...", etc.) get a smaller font so they never clip
        IntPtr smallFont = CreateSegoeFont(-13, FW_NORMAL);
        IntPtr footerFont = CreateSegoeFont(-12, FW_NORMAL);

        DrawCenteredText(hdc, titleFont, "CreditMeter", 6, 18, rect.Right, 0x00B0B0B0);
        DrawCenteredText(hdc, valueFont, content.MainText, 24, 46, rect.Right, MeterColors.Amber); // taxi-meter amber
        DrawCenteredText(hdc, smallFont, content.Subtitle, 70, 18, rect.Right, 0x00D8D8D8);

        if (content.ShowProgress)
        {
            DrawProgressBar(hdc, rect, 92, content.ProgressFraction);
        }

        DrawCenteredText(hdc, footerFont, content.Footer, 106, 18, rect.Right, 0x00909090);

        _ = DeleteObject(titleFont);
        _ = DeleteObject(valueFont);
        _ = DeleteObject(smallFont);
        _ = DeleteObject(footerFont);

        _ = EndPaint(hWnd, ref ps);
    }

    /// <summary>
    /// Draws a tiny flat progress bar: a light outline rectangle with a solid
    /// fill proportional to <paramref name="fraction"/> (0.0–1.0). No gradients,
    /// no animation, no image assets — just three FillRect calls.
    /// </summary>
    private static void DrawProgressBar(IntPtr hdc, RECT clientRect, int top, double fraction)
    {
        const int barHeight = 8;
        const int margin = 20;

        var outer = new RECT { Left = margin, Top = top, Right = clientRect.Right - margin, Bottom = top + barHeight };

        IntPtr border = CreateSolidBrush(0x00606060); // dim gray outline against the dark screen
        _ = FillRect(hdc, ref outer, border);
        _ = DeleteObject(border);

        var inner = new RECT { Left = outer.Left + 1, Top = outer.Top + 1, Right = outer.Right - 1, Bottom = outer.Bottom - 1 };
        IntPtr track = CreateSolidBrush(MeterColors.Glass); // matches the meter "glass" background
        _ = FillRect(hdc, ref inner, track);
        _ = DeleteObject(track);

        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        int fillWidth = (int)((inner.Right - inner.Left) * clamped);
        if (fillWidth > 0)
        {
            var fill = new RECT { Left = inner.Left, Top = inner.Top, Right = inner.Left + fillWidth, Bottom = inner.Bottom };

            // Green while under the limit, red once it's fully used — like a meter dial redlining.
            int fillColor = clamped >= 1.0 ? MeterColors.Red : MeterColors.Green;
            IntPtr fillBrush = CreateSolidBrush(fillColor);
            _ = FillRect(hdc, ref fill, fillBrush);
            _ = DeleteObject(fillBrush);
        }
    }

    private static IntPtr CreateSegoeFont(int height, int weight)
    {
        return CreateFont(
            height, 0, 0, 0, weight,
            0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
            DEFAULT_PITCH | FF_SWISS, "Segoe UI");
    }

    private static void DrawCenteredText(IntPtr hdc, IntPtr font, string text, int top, int height, int width, int colorBgr)
    {
        IntPtr oldFont = SelectObject(hdc, font);
        _ = SetTextColor(hdc, colorBgr);

        var rect = new RECT { Left = 8, Top = top, Right = width - 8, Bottom = top + height };
        _ = DrawText(hdc, text, text.Length, ref rect, DT_CENTER | DT_SINGLELINE | DT_VCENTER);

        _ = SelectObject(hdc, oldFont);
    }

    /// <summary>Everything the paint routine needs to render one popup frame.</summary>
    private readonly record struct PopupContent(string MainText, string Subtitle, string Footer, bool ShowProgress, double ProgressFraction, bool IsFare = false);

    /// <summary>Maps the shared poll state to the content to render.</summary>
    private static PopupContent DescribeState(CreditState? state)
    {
        const string defaultFooter = "Updates every 5 minutes";

        if (state is null || !state.IsConfigured)
        {
            return new PopupContent("Not configured", "Set PAT and username first", defaultFooter, false, 0);
        }

        if (state.IsLoading)
        {
            return new PopupContent("Loading...", "Checking GitHub usage", defaultFooter, false, 0);
        }

        if (state.ApiUnavailable)
        {
            string subtitle = state.LastError ?? "Unknown error";
            return new PopupContent("API unavailable", subtitle, defaultFooter, false, 0);
        }

        if (state.CurrentMonthSpend is null || state.CurrentMonthCredits is null)
        {
            return new PopupContent("Loading...", "Checking GitHub usage", defaultFooter, false, 0);
        }

        decimal spend = state.CurrentMonthSpend.Value;
        decimal credits = state.CurrentMonthCredits.Value;

        string amount = "$" + spend.ToString("0.00", CultureInfo.InvariantCulture);
        string subtitleText = $"{Program.FormatCredits(credits)} AI credits burned";

        if (state.MonthlyCreditLimit is not decimal limit)
        {
            return new PopupContent(amount, subtitleText, "meter running...", false, 0, IsFare: true);
        }

        if (limit <= 0 || credits >= limit)
        {
            return new PopupContent(amount, subtitleText, "Limit reached", true, 1.0, IsFare: true);
        }

        decimal remaining = limit - credits;
        double fraction = (double)(credits / limit);
        string footer = $"{Program.FormatCredits(remaining)} credits left of {Program.FormatCredits(limit)}";
        return new PopupContent(amount, subtitleText, footer, true, fraction, IsFare: true);
    }
}

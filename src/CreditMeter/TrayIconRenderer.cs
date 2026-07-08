using static CreditMeter.NativeMethods;

namespace CreditMeter;

/// <summary>
/// Generates the tray icon entirely in code via raw Win32/GDI — no .ico
/// asset, no icon designer file. Draws a tiny dark "taxi meter" square with
/// a "$" mark that is color-coded by usage (green/amber/red), so the icon
/// itself communicates state at a glance without opening the popup.
///
/// Every GDI handle created here is cleaned up before returning; the only
/// handle the caller owns afterward is the returned HICON, which must be
/// destroyed (via DestroyIcon) once it has been replaced or the app exits.
/// </summary>
internal static class TrayIconRenderer
{
    private const int Size = 32;

    private const int ColorGlass = 0x00100C0C;      // near-black meter "glass" background
    private const int ColorAmber = 0x0000C8FF;      // default/neutral "taxi meter amber"
    private const int ColorGreen = 0x0000C060;      // low usage
    private const int ColorRed = 0x000000E0;        // at/over limit
    private const int ColorTrack = 0x00303030;      // progress bar track
    private const int ColorAlertBadge = 0x000000C0; // "!" badge background
    private const int ColorWhite = 0x00FFFFFF;

    /// <summary>
    /// Builds a fresh HICON reflecting <paramref name="state"/>. Returns
    /// <see cref="IntPtr.Zero"/> if icon generation fails for any reason —
    /// callers must fall back to the stock icon rather than crash. Icon
    /// drawing errors are strictly separate from API/ApiUnavailable state
    /// and never touch <see cref="CreditState"/>.
    /// </summary>
    public static IntPtr Render(CreditState? state)
    {
        try
        {
            return RenderCore(state);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr RenderCore(CreditState? state)
    {
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hdcMono = IntPtr.Zero;
        IntPtr hbmColor = IntPtr.Zero;
        IntPtr hbmMask = IntPtr.Zero;

        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            hbmColor = CreateCompatibleBitmap(hdcScreen, Size, Size);
            if (hdcMem == IntPtr.Zero || hbmColor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr oldColorBmp = SelectObject(hdcMem, hbmColor);
            DrawIconContent(hdcMem, state);
            SelectObject(hdcMem, oldColorBmp);

            // Fully opaque mask (all bits 0). Since hbmColor has more than
            // 1 bit per pixel, Windows uses its pixels directly wherever the
            // mask is 0 rather than XOR-combining, so an all-zero mask gives
            // us a plain opaque square with no transparency math needed.
            hbmMask = CreateBitmap(Size, Size, 1, 1, IntPtr.Zero);
            if (hbmMask == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            hdcMono = CreateCompatibleDC(hdcScreen);
            if (hdcMono == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr oldMonoBmp = SelectObject(hdcMono, hbmMask);
            PatBlt(hdcMono, 0, 0, Size, Size, BLACKNESS);
            SelectObject(hdcMono, oldMonoBmp);

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hbmMask,
                hbmColor = hbmColor
            };

            // CreateIconIndirect makes its own copies of hbmColor/hbmMask,
            // so both are safe to delete in the finally block below.
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcMono != IntPtr.Zero) DeleteDC(hdcMono);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// Draws the actual icon pixels: a dark background, a "$" mark
    /// color-coded by usage when known, plus a thin bottom progress bar or
    /// a small "!" badge for the handful of states that need one. Kept
    /// deliberately simple — no gradients, no assets.
    /// </summary>
    private static void DrawIconContent(IntPtr hdc, CreditState? state)
    {
        var full = new RECT { Left = 0, Top = 0, Right = Size, Bottom = Size };
        IntPtr background = CreateSolidBrush(ColorGlass);
        FillRect(hdc, ref full, background);
        DeleteObject(background);

        SetBkMode(hdc, TRANSPARENT_BKMODE);

        if (state is not null && state.ApiUnavailable)
        {
            DrawDollarMark(hdc, ColorAmber);
            DrawAlertBadge(hdc);
            return;
        }

        if (state?.CurrentMonthCredits is decimal credits &&
            state.MonthlyCreditLimit is decimal limit && limit > 0)
        {
            double fraction = (double)(credits / limit);
            int color = fraction >= 1.0 ? ColorRed : fraction >= 0.7 ? ColorAmber : ColorGreen;
            DrawDollarMark(hdc, color);
            DrawProgressBar(hdc, fraction, color);
            return;
        }

        // Stable default: startup, not-yet-configured, still loading, or
        // configured without a monthly limit to compare against.
        DrawDollarMark(hdc, ColorAmber);
    }

    private static void DrawDollarMark(IntPtr hdc, int colorBgr)
    {
        IntPtr font = CreateFont(
            -22, 0, 0, 0, FW_BOLD,
            0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
            DEFAULT_PITCH | FF_SWISS, "Segoe UI");

        IntPtr oldFont = SelectObject(hdc, font);
        SetTextColor(hdc, colorBgr);

        var rect = new RECT { Left = 0, Top = 0, Right = Size, Bottom = Size };
        DrawText(hdc, "$", 1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        SelectObject(hdc, oldFont);
        DeleteObject(font);
    }

    /// <summary>Thin bar along the bottom edge, filled proportionally to usage — a tiny gauge.</summary>
    private static void DrawProgressBar(IntPtr hdc, double fraction, int colorBgr)
    {
        const int margin = 3;
        const int barTop = Size - 6;
        const int barBottom = Size - 3;

        var track = new RECT { Left = margin, Top = barTop, Right = Size - margin, Bottom = barBottom };
        IntPtr trackBrush = CreateSolidBrush(ColorTrack);
        FillRect(hdc, ref track, trackBrush);
        DeleteObject(trackBrush);

        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        int fillWidth = (int)((track.Right - track.Left) * clamped);
        if (fillWidth <= 0)
        {
            return;
        }

        var fill = new RECT { Left = track.Left, Top = track.Top, Right = track.Left + fillWidth, Bottom = track.Bottom };
        IntPtr fillBrush = CreateSolidBrush(colorBgr);
        FillRect(hdc, ref fill, fillBrush);
        DeleteObject(fillBrush);
    }

    /// <summary>Small red circle with a white "!" in the top-right corner, used when the API is unavailable.</summary>
    private static void DrawAlertBadge(IntPtr hdc)
    {
        const int r = 7;
        const int cx = Size - 9;
        const int cy = 9;

        IntPtr brush = CreateSolidBrush(ColorAlertBadge);
        IntPtr pen = CreatePen(PS_SOLID, 1, ColorAlertBadge);
        IntPtr oldBrush = SelectObject(hdc, brush);
        IntPtr oldPen = SelectObject(hdc, pen);

        Ellipse(hdc, cx - r, cy - r, cx + r, cy + r);

        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);

        IntPtr font = CreateFont(
            -12, 0, 0, 0, FW_BOLD,
            0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
            DEFAULT_PITCH | FF_SWISS, "Segoe UI");

        IntPtr oldFont = SelectObject(hdc, font);
        SetTextColor(hdc, ColorWhite);

        var rect = new RECT { Left = cx - r, Top = cy - r, Right = cx + r, Bottom = cy + r };
        DrawText(hdc, "!", 1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        SelectObject(hdc, oldFont);
        DeleteObject(font);
    }
}

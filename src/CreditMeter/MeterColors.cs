namespace CreditMeter;

/// <summary>
/// Shared 0x00BBGGRR color constants for the "taxi meter" look, used by both
/// <see cref="MeterPopupWindow"/> and <see cref="TrayIconRenderer"/> so the
/// popup and the tray icon always agree on what "neutral/green/amber/red"
/// look like instead of each maintaining its own copy of the same values.
/// </summary>
internal static class MeterColors
{
    public const int Glass = 0x00100C0C;  // near-black meter "glass" background
    public const int Track = 0x00303030;  // dark gray bezel / progress-bar track
    public const int Amber = 0x0000C8FF;  // default/neutral "taxi meter amber"
    public const int Green = 0x0000C060;  // low usage
    public const int Red = 0x000000E0;    // at/over limit
}

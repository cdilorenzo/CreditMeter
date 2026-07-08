using System.Globalization;
using System.Runtime.InteropServices;
using static CreditMeter.NativeMethods;

namespace CreditMeter;

/// <summary>
/// Result of resolving the PAT/username needed to call the GitHub API.
/// <see cref="MissingReason"/> is a short, user-facing explanation (never
/// contains the PAT) set only when <see cref="IsConfigured"/> is false.
/// </summary>
internal readonly record struct ApiInputs(string? Pat, string? Username, bool IsConfigured, string? MissingReason);

internal static class Program
{
    // Keep a static reference to the delegate so the GC never collects it
    // while unmanaged code (the Windows message loop) still holds a pointer to it.
    private static readonly WndProc s_wndProc = WindowProc;

    private const string ClassName = "CreditMeterTrayWindowClass";
    private const uint TrayIconId = 1;
    private const uint MenuIdRetry = 999;
    private const uint MenuIdExit = 1000;

    private static IntPtr s_hWnd;
    private static IntPtr s_hInstance;
    private static CancellationTokenSource? s_pollingCts;
    private static readonly CreditState s_state = new();

    // The HICON we generated ourselves (via TrayIconRenderer) currently shown
    // in the tray, if any. Zero when the stock fallback icon is in use — the
    // stock icon is a shared system resource and must never be destroyed.
    private static IntPtr s_trayIconHandle;

    // Set once when configured at startup; read by both the periodic loop and
    // manual "Retry now" so they always poll with the exact same inputs.
    private static GitHubApiClient? s_client;
    private static string? s_username;
    private static string? s_pat;
    private static decimal? s_monthlyCreditLimit;

    // Guard against overlapping API calls (periodic tick racing a manual retry).
    // 0 = idle, 1 = a poll is in flight.
    private static int s_pollInProgress;

    private static int Main(string[] args)
    {
        if (TryHandleHelpCommand(args))
        {
            return 0;
        }

        if (TryHandleSetPatCommand(args))
        {
            return 0;
        }

        if (TryHandleSetPatEnvCommand(args))
        {
            return 0;
        }

        if (TryHandleSetUsernameCommand(args))
        {
            return 0;
        }

        if (TryHandleTestApiCommand(args))
        {
            return 0;
        }

        if (TryHandleSetCreditLimitCommand(args))
        {
            return 0;
        }

        if (TryHandleClearCreditLimitCommand(args))
        {
            return 0;
        }

        AppSettings settings = SettingsStore.Load();

        IntPtr hInstance = GetModuleHandle(null);
        s_hInstance = hInstance;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            return -1;
        }

        // Message-only-ish window: we never show it, it just exists to receive
        // tray callback messages and own the message loop.
        s_hWnd = CreateWindowEx(
            0, ClassName, "CreditMeter", 0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (s_hWnd == IntPtr.Zero)
        {
            return -1;
        }

        AddTrayIcon(s_hWnd, BuildStartupTooltip(settings));

        StartPollingIfConfigured(settings);

        // Standard Win32 message pump. This blocks until WM_QUIT.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        s_pollingCts?.Cancel();
        MeterPopupWindow.Hide();
        RemoveTrayIcon(s_hWnd);
        return 0;
    }

    /// <summary>
    /// Resolves the PAT and GitHub username the same way for every caller
    /// (--test-api, normal startup polling, and "Retry now") so they never
    /// drift apart. For --test-api, <paramref name="args"/> may contain a
    /// --username override; normal runtime callers pass an empty array and
    /// always use <see cref="AppSettings.GitHubUsername"/>.
    /// </summary>
    private static ApiInputs ResolveApiInputs(AppSettings settings, string[] args)
    {
        string? pat = SettingsStore.GetPlainTextPat(settings);
        string? username = GetArgValue(args, "--username") ?? settings.GitHubUsername;

        if (pat is null)
        {
            return new ApiInputs(null, username, false, "No PAT configured. Run --set-pat first.");
        }

        if (string.IsNullOrEmpty(username))
        {
            return new ApiInputs(pat, null, false, "No username configured. Run --set-username first or pass --username.");
        }

        return new ApiInputs(pat, username, true, null);
    }

    /// <summary>
    /// Starts a background loop that polls the Copilot spend API every few
    /// minutes and keeps the tray tooltip updated. If the PAT or GitHub
    /// username aren't configured yet, the API is never called — state is
    /// set to "not configured" instead of "API unavailable".
    /// </summary>
    private static void StartPollingIfConfigured(AppSettings settings)
    {
        ApiInputs inputs = ResolveApiInputs(settings, Array.Empty<string>());

        if (!inputs.IsConfigured)
        {
            s_state.IsConfigured = false;
            UpdateTrayTooltip("CreditMeter: configure PAT and username");
            RefreshTrayIcon(s_state);
            MeterPopupWindow.NotifyStateChanged();
            return;
        }

        s_state.IsConfigured = true;
        s_state.IsLoading = true;
        s_username = inputs.Username;
        s_pat = inputs.Pat;
        s_monthlyCreditLimit = settings.MonthlyCreditLimit;
        s_state.MonthlyCreditLimit = s_monthlyCreditLimit;
        s_client = new GitHubApiClient();

        UpdateTrayTooltip("CreditMeter: updating...");
        RefreshTrayIcon(s_state);
        MeterPopupWindow.NotifyStateChanged();

        s_pollingCts = new CancellationTokenSource();
        _ = PollLoopAsync(s_pollingCts.Token);
    }

    /// <summary>
    /// Polls every 5 minutes, starting with an immediate poll right away.
    /// "Retry now" from the tray menu drives the exact same
    /// <see cref="TriggerPollAsync"/> helper, so the two never drift apart.
    /// </summary>
    private static async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        do
        {
            await TriggerPollAsync().ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one poll, guarding against overlapping calls (e.g. the periodic
    /// timer ticking while a manual "Retry now" is still in flight). No-ops
    /// if not configured or if a poll is already running.
    /// </summary>
    private static async Task TriggerPollAsync()
    {
        if (s_client is null || s_username is null || s_pat is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_pollInProgress, 1, 0) != 0)
        {
            // A poll is already running — ignore this request rather than
            // starting an overlapping API call.
            return;
        }

        try
        {
            await PollCopilotSpendOnceAsync(s_client, s_username, s_pat, s_state).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref s_pollInProgress, 0);
        }
    }

    /// <summary>
    /// Shared single-poll logic used by both the periodic loop and manual
    /// "Retry now" so their behavior never drifts apart. Sets Loading before
    /// the call, updates state/tooltip/popup on completion, and never lets
    /// an API failure escape and crash the tray app.
    /// </summary>
    private static async Task PollCopilotSpendOnceAsync(GitHubApiClient client, string username, string pat, CreditState state)
    {
        state.IsLoading = true;
        UpdateTrayTooltip("CreditMeter: updating...");
        RefreshTrayIcon(state);
        MeterPopupWindow.NotifyStateChanged();

        try
        {
            GitHubSpendResult result = await client.GetCurrentMonthNetSpendDetailedAsync(username, pat).ConfigureAwait(false);
            if (result.Spend is not null && result.Credits is not null)
            {
                state.CurrentMonthSpend = result.Spend;
                state.CurrentMonthCredits = result.Credits;
                state.MonthlyCreditLimit = s_monthlyCreditLimit;
                state.ApiUnavailable = false;
                state.LastError = null;
                state.LastUpdated = DateTimeOffset.UtcNow;
                UpdateTrayTooltip(BuildUsageTooltip(result.Spend.Value, result.Credits.Value, s_monthlyCreditLimit));
                RefreshTrayIcon(state);
            }
            else
            {
                state.ApiUnavailable = true;
                state.LastError = result.Error ?? "API returned no spend";
                UpdateTrayTooltip("CreditMeter: API unavailable");
                RefreshTrayIcon(state);
            }
        }
        catch (Exception)
        {
            // Never let a poll failure take down the tray app.
            state.ApiUnavailable = true;
            state.LastError = "Network error";
            UpdateTrayTooltip("CreditMeter: API unavailable");
            RefreshTrayIcon(state);
        }
        finally
        {
            state.IsLoading = false;
            MeterPopupWindow.NotifyStateChanged();
        }
    }

    /// <summary>
    /// Temporary way to configure CreditMeter before a real settings UI exists.
    /// Usage: CreditMeter.exe --set-pat &lt;github-pat&gt; --org &lt;org-name&gt;
    /// Saves the PAT encrypted via DPAPI and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleSetPatCommand(string[] args)
    {
        string? pat = GetArgValue(args, "--set-pat");
        if (pat is null)
        {
            return false;
        }

        AppSettings settings = SettingsStore.Load();
        SettingsStore.SetPat(settings, pat);

        string? org = GetArgValue(args, "--org");
        if (org is not null)
        {
            settings.OrgName = org;
        }

        SettingsStore.Save(settings);

        MessageBox(IntPtr.Zero,
            "Saved. The PAT is encrypted at rest with DPAPI and tied to this Windows account.",
            "CreditMeter", 0);

        return true;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        int idx = Array.IndexOf(args, flag);
        return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
    }

    /// <summary>
    /// Alternative to --set-pat that reads the token from an environment
    /// variable instead of the command line, so it never appears in shell
    /// history or a process list.
    /// Usage: CreditMeter.exe --set-pat-env &lt;env-var-name&gt;
    /// Saves the PAT encrypted via DPAPI and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleSetPatEnvCommand(string[] args)
    {
        string? envVarName = GetArgValue(args, "--set-pat-env");
        if (envVarName is null)
        {
            return false;
        }

        string? pat = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(pat))
        {
            MessageBox(IntPtr.Zero, $"Environment variable '{envVarName}' is not set or empty.", "CreditMeter", 0);
            return true;
        }

        AppSettings settings = SettingsStore.Load();
        SettingsStore.SetPat(settings, pat);
        SettingsStore.Save(settings);

        MessageBox(IntPtr.Zero,
            "Saved. The PAT is encrypted at rest with DPAPI and tied to this Windows account.",
            "CreditMeter", 0);

        return true;
    }

    /// <summary>
    /// Usage: CreditMeter.exe --help
    /// Lists the available commands and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleHelpCommand(string[] args)
    {
        if (Array.IndexOf(args, "--help") < 0)
        {
            return false;
        }

        const string helpText = """
            CreditMeter commands:
            --set-pat <token>
            --set-pat-env <env-var-name>
            --set-username <username>
            --set-credit-limit <credits>
            --clear-credit-limit
            --test-api
            --debug-api
            --help
            """;

        MessageBox(IntPtr.Zero, helpText, "CreditMeter", 0);

        return true;
    }

    /// <summary>
    /// Temporary way to configure the GitHub username before a real settings UI exists.
    /// Usage: CreditMeter.exe --set-username &lt;github-username&gt;
    /// Saves the username and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleSetUsernameCommand(string[] args)
    {
        string? username = GetArgValue(args, "--set-username");
        if (username is null)
        {
            return false;
        }

        AppSettings settings = SettingsStore.Load();
        settings.GitHubUsername = username;
        SettingsStore.Save(settings);

        MessageBox(IntPtr.Zero, "GitHub username saved.", "CreditMeter", 0);

        return true;
    }

    /// <summary>
    /// Sets the local-only monthly AI credit limit before a real settings UI
    /// exists. Usage: CreditMeter.exe --set-credit-limit &lt;number&gt;
    /// Saves the parsed limit and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleSetCreditLimitCommand(string[] args)
    {
        string? rawValue = GetArgValue(args, "--set-credit-limit");
        if (rawValue is null)
        {
            return false;
        }

        if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal limit) || limit < 0)
        {
            MessageBox(IntPtr.Zero, "Invalid credit limit.", "CreditMeter", 0);
            return true;
        }

        AppSettings settings = SettingsStore.Load();
        settings.MonthlyCreditLimit = limit;
        SettingsStore.Save(settings);

        MessageBox(IntPtr.Zero, "Monthly credit limit saved.", "CreditMeter", 0);

        return true;
    }

    /// <summary>
    /// Clears the local-only monthly AI credit limit.
    /// Usage: CreditMeter.exe --clear-credit-limit
    /// Saves and exits — does not start the tray icon.
    /// </summary>
    private static bool TryHandleClearCreditLimitCommand(string[] args)
    {
        if (Array.IndexOf(args, "--clear-credit-limit") < 0)
        {
            return false;
        }

        AppSettings settings = SettingsStore.Load();
        settings.MonthlyCreditLimit = null;
        SettingsStore.Save(settings);

        MessageBox(IntPtr.Zero, "Monthly credit limit cleared.", "CreditMeter", 0);

        return true;
    }

    /// <summary>
    /// Temporary way to smoke-test the GitHub billing API call before the real
    /// polling/UI wiring exists.
    /// Usage: CreditMeter.exe --test-api [--username &lt;github-username&gt;] [--debug-api]
    /// Requires a PAT already saved via --set-pat. Falls back to the saved
    /// GitHubUsername setting if --username isn't passed. Pass --debug-api to
    /// also print the sanitized request URL, HTTP status, and a response body
    /// snippet — never the PAT. Does not start the tray icon.
    /// </summary>
    private static bool TryHandleTestApiCommand(string[] args)
    {
        if (Array.IndexOf(args, "--test-api") < 0)
        {
            return false;
        }

        bool debug = Array.IndexOf(args, "--debug-api") >= 0;

        AppSettings settings = SettingsStore.Load();
        ApiInputs inputs = ResolveApiInputs(settings, args);
        if (!inputs.IsConfigured)
        {
            MessageBox(IntPtr.Zero, inputs.MissingReason ?? "Not configured.", "CreditMeter", 0);
            return true;
        }

        var client = new GitHubApiClient();
        GitHubSpendResult result = client.GetCurrentMonthNetSpendDetailedAsync(inputs.Username!, inputs.Pat!).GetAwaiter().GetResult();

        // Sanitized diagnostics — request URL, HTTP status, and a truncated
        // response body snippet. Never includes the PAT. Only shown with
        // --debug-api so normal --test-api output stays short.
        string diagnostics = debug
            ? $"[Diagnostics]\nRequest: GET {result.RequestUrl}\nStatus: {(result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}\nBody (first 1000 chars):\n{result.ResponseBodySnippet ?? "(none)"}\n\n"
            : string.Empty;

        if (result.Spend is not null && result.Credits is not null)
        {
            string formattedSpend = "$" + result.Spend.Value.ToString("0.00", CultureInfo.InvariantCulture);
            string formattedCredits = FormatCredits(result.Credits.Value);

            string message = $"{diagnostics}This month's Copilot spend: {formattedSpend}\nAI credits used: {formattedCredits}";

            MessageBox(IntPtr.Zero, message, "CreditMeter", 0);
        }
        else
        {
            string reason = result.Error ?? "API returned no spend";
            MessageBox(IntPtr.Zero, $"{diagnostics}API call failed: {reason}", "CreditMeter", 0);
        }

        return true;
    }

    private static string BuildStartupTooltip(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.EncryptedPat))
        {
            return "CreditMeter — not configured";
        }

        return string.IsNullOrEmpty(settings.OrgName)
            ? "CreditMeter — configured"
            : $"CreditMeter — {settings.OrgName}";
    }

    /// <summary>
    /// Builds the tray tooltip text once spend/credits are known, e.g.
    /// "CreditMeter: $2.14 / 214 credits this month" or, with a configured
    /// local monthly limit (only if the user explicitly set one via
    /// --set-credit-limit), "CreditMeter: $2.14 / 214 of 500 credits".
    /// </summary>
    private static string BuildUsageTooltip(decimal spend, decimal credits, decimal? monthlyLimit)
    {
        string dollars = "$" + spend.ToString("0.00", CultureInfo.InvariantCulture);
        string creditsStr = FormatCredits(credits);

        if (monthlyLimit is null)
        {
            return $"CreditMeter: {dollars} / {creditsStr} credits this month";
        }

        string limitStr = FormatCredits(monthlyLimit.Value);
        return $"CreditMeter: {dollars} / {creditsStr} of {limitStr} credits";
    }

    /// <summary>
    /// Formats a credit amount as a whole number when it has no fractional
    /// part, otherwise with one decimal place — keeps tooltip/popup text short.
    /// </summary>
    internal static string FormatCredits(decimal value)
    {
        return value == Math.Truncate(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void AddTrayIcon(IntPtr hWnd, string tooltip)
    {
        // Startup default: state isn't resolved yet, so this always renders
        // the stable neutral "$" icon. StartPollingIfConfigured refreshes it
        // moments later once configuration/API status is known.
        IntPtr generatedIcon = TrayIconRenderer.Render(null);
        IntPtr iconToShow = generatedIcon != IntPtr.Zero
            ? generatedIcon
            : LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAYCALLBACK,
            hIcon = iconToShow,
            szTip = tooltip
        };

        Shell_NotifyIcon(NIM_ADD, ref nid);
        s_trayIconHandle = generatedIcon;
    }

    /// <summary>Call this later from the polling loop to update the tray tooltip live.</summary>
    public static void UpdateTrayTooltip(string tooltip)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = s_hWnd,
            uID = TrayIconId,
            uFlags = NIF_TIP,
            szTip = tooltip
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Rebuilds the tray icon from <paramref name="state"/> (see
    /// <see cref="TrayIconRenderer"/>) and swaps it into the tray. Falls back
    /// to the stock application icon if generation fails — icon drawing
    /// problems must never crash the app or be conflated with the separate
    /// ApiUnavailable/LastError state. Always destroys the previously
    /// generated icon (if any) so GDI handles never leak.
    /// </summary>
    private static void RefreshTrayIcon(CreditState state)
    {
        IntPtr generatedIcon = TrayIconRenderer.Render(state);
        IntPtr iconToShow = generatedIcon != IntPtr.Zero
            ? generatedIcon
            : LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = s_hWnd,
            uID = TrayIconId,
            uFlags = NIF_ICON,
            hIcon = iconToShow
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);

        // Only ever destroy icons we generated ourselves — never the shared
        // stock IDI_APPLICATION icon.
        if (s_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(s_trayIconHandle);
        }

        s_trayIconHandle = generatedIcon;
    }

    private static void RemoveTrayIcon(IntPtr hWnd)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId
        };

        Shell_NotifyIcon(NIM_DELETE, ref nid);

        if (s_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(s_trayIconHandle);
            s_trayIconHandle = IntPtr.Zero;
        }
    }

    private static void ShowContextMenu(IntPtr hWnd)
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuIdRetry, "Retry now");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, 0, MenuIdExit, "Exit");

        GetCursorPos(out POINT pt);

        // Required so the menu closes if the user clicks away from it.
        SetForegroundWindow(hWnd);

        uint cmd = TrackPopupMenuEx(
            menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.X, pt.Y, hWnd, IntPtr.Zero);

        DestroyMenu(menu);

        if (cmd == MenuIdExit)
        {
            PostMessage(hWnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }
        else if (cmd == MenuIdRetry)
        {
            // Fire-and-forget: must not block the Win32 message loop.
            // TriggerPollAsync's own guard prevents overlapping API calls.
            _ = TriggerPollAsync();
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_TRAYCALLBACK:
                // lParam carries the mouse event that occurred on the tray icon.
                if ((int)lParam == WM_RBUTTONUP)
                {
                    ShowContextMenu(hWnd);
                }
                else if ((int)lParam == WM_LBUTTONUP)
                {
                    MeterPopupWindow.Toggle(s_hInstance, s_state);
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}

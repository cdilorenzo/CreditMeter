# CreditMeter

A tiny Windows tray app that watches your GitHub Copilot AI-credit usage and
shows it like a little taxi meter.

No WinForms. No WPF. No dashboard. Just raw Win32, encrypted local settings,
and a playful meter popup.

![CreditMeter demo](docs/creditmeter-demo.gif)

## What it does

CreditMeter runs in the Windows system tray and periodically reads your current
month GitHub Copilot AI-credit usage.

It shows:

- current month spend, for example `$4.27`
- AI credits used, for example `427 AI credits burned`
- optional local monthly credit limit/progress
- a tiny taxi-meter style popup on tray left-click
- a tray tooltip with the current usage
- a manual **Retry now** action from the tray menu

The goal is not to be a full GitHub billing dashboard. CreditMeter is a small,
fun, local utility that makes AI-credit burn visible.

## Privacy

CreditMeter is local-only.

- Your GitHub PAT is stored locally in `%APPDATA%\CreditMeter\settings.json`
- The PAT is encrypted at rest using Windows DPAPI
- No telemetry
- No external service except GitHub's API
- No analytics
- No account system

## Requirements

- Windows
- .NET 9 SDK for building from source
- A GitHub personal access token with access to the relevant Copilot billing /
  usage endpoint

## Repo layout

```text
CreditMeter/
├── README.md
├── LICENSE
├── .gitignore
├── docs/
│   └── creditmeter-demo.gif
└── src/
    └── CreditMeter/
        ├── CreditMeter.csproj
        ├── app.manifest
        ├── Program.cs
        ├── NativeMethods.cs
        ├── Settings.cs
        ├── GitHubApiClient.cs
        ├── CreditState.cs
        └── MeterPopupWindow.cs
```

## Setup

Clone and build:

```powershell
git clone https://github.com/<you>/CreditMeter.git
cd CreditMeter
dotnet build src/CreditMeter/CreditMeter.csproj
```

Configure your GitHub username:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-username cdilorenzo
```

Configure your GitHub PAT safely from an environment variable:

```powershell
$env:CREDITMETER_PAT = "github_pat_REPLACE_ME"
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-pat-env CREDITMETER_PAT
```

You can also set the PAT directly, but using an environment variable avoids
putting the token in your shell history:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-pat <your-github-pat>
```

Optional: set a local monthly AI-credit limit. This does not change anything on
GitHub. It is only used for the local meter display.

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-credit-limit 1500
```

Clear the local limit again:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --clear-credit-limit
```

## Test the GitHub API connection

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --test-api
```

Expected output is a message box showing something like:

```text
This month's Copilot spend: $4.27
AI credits used: 427
```

For sanitized debugging output:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --debug-api
```

Diagnostics must never include your PAT.

## Run CreditMeter

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj
```

Behavior:

- CreditMeter starts in the system tray
- left-click the tray icon to toggle the meter popup
- right-click the tray icon to open the menu
- choose **Retry now** to refresh usage immediately
- choose **Exit** to quit

The app polls GitHub every few minutes and updates the tray tooltip, tray icon,
and popup state.

## CLI commands

```text
--set-pat <token>              Save GitHub PAT locally using DPAPI encryption
--set-pat-env <env-var-name>   Save GitHub PAT from an environment variable
--set-username <username>      Save GitHub username
--set-credit-limit <credits>   Save optional local monthly AI-credit limit
--clear-credit-limit           Remove local monthly AI-credit limit
--test-api                     Test GitHub usage API with clean output
--debug-api                    Test GitHub usage API with sanitized diagnostics
--help                         Show available commands
```

## Publish as a single Windows executable

```powershell
dotnet publish src/CreditMeter/CreditMeter.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true
```

The published executable is created under:

```text
src/CreditMeter/bin/Release/net9.0-windows/win-x64/publish/
```

Run the published `CreditMeter.exe` once locally before creating a release.

## Tiny Tool Town pitch

CreditMeter is a tiny taxi meter for AI spending.

It sits in your Windows tray and shows your GitHub Copilot AI-credit burn in a
small playful popup, so agentic coding suddenly feels like watching a cab fare
tick upward.

## Before publishing

Before making the repo public or recording a demo GIF:

- rotate any PAT that was ever shown in screenshots or terminal output
- make sure `settings.json` is not committed
- make sure `bin/`, `obj/`, and publish output are ignored
- record a short GIF showing:
  - the tray icon
  - left-click popup
  - AI credits used
  - optional progress bar
  - right-click menu with Retry now / Exit

## License

MIT

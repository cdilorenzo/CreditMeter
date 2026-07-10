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
- a tray icon that is itself color-coded (green/amber/red) by usage against
  your optional local limit, with a small "!" badge if the API call fails
- a tray tooltip with the current usage
- a manual **Retry now** action from the tray menu

The goal is not to be a full GitHub billing dashboard. CreditMeter is a small,
fun, local utility that makes AI-credit burn visible.

## Supported accounts

CreditMeter supports two usage scopes (see `--set-scope` below):

- **user** (default) — GitHub's **personal** billing endpoint:

  ```text
  GET /users/{username}/settings/billing/ai_credit/usage
  ```

  This endpoint only returns data if you purchased **your own individual**
  Copilot plan.

- **org** — GitHub's **organization** billing endpoint, for accounts whose
  Copilot access is provided and billed through a GitHub organization (the
  common case for company-managed accounts):

  ```text
  GET /organizations/{org}/settings/billing/ai_credit/usage   (requires org admin access)
  ```

See [Organization mode](#organization-mode) below for how to switch scopes.

## Organization mode

If your Copilot access is billed through a GitHub organization rather than
your own individual plan, switch CreditMeter to org scope:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-scope org
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-org my-org
```

Optionally narrow usage to a single org member instead of the whole
organization:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --set-org-user someuser
```

Clear the member filter again to go back to whole-org usage:

```powershell
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --clear-org-user
```

Switch back to personal usage at any time with `--set-scope user`.

Org mode calls:

```text
GET /organizations/{org}/settings/billing/ai_credit/usage?year=YYYY&month=M
```

This requires a PAT with organization **Administration** read permission
(not the personal "Plan" permission). If the PAT lacks that permission, the
tray/popup show a sanitized hint — "API unavailable: org admin permission
required" — instead of the raw HTTP error.

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
- A GitHub personal access token: fine-grained with the "Plan" read-only user
  permission for personal (`user` scope) usage, or with organization
  **Administration** read permission for organization-billed (`org` scope)
  usage — see [Supported accounts](#supported-accounts) and
  [Organization mode](#organization-mode) above

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
        ├── MeterColors.cs
        ├── TrayIconRenderer.cs
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
dotnet run --project src/CreditMeter/CreditMeter.csproj -- --test-api --debug-api
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
--set-scope user               Use personal Copilot AI-credit usage
--set-scope org                Use organization-billed Copilot AI-credit usage
--set-org <org>                Save the organization login for org scope
--set-org-user <username>      Narrow org usage to one member
--clear-org-user               Remove the org member filter (whole-org usage)
--test-api                     Test GitHub usage API with clean output
--debug-api                    Add sanitized diagnostics; only takes effect with --test-api
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

## License

MIT

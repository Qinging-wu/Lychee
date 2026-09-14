# Contributing to Lychee

Thanks for your interest in Lychee! Bug reports, feature ideas, documentation improvements, and pull requests are welcome.

## Development setup

- Windows
- .NET SDK 8.0 or later
- WPF and WinForms workloads

Build the project with:

```powershell
dotnet build -c Release
```

For a publish build:

```powershell
dotnet publish -c Release -r win-x64
```

## Issues

Before opening an issue, check whether it has already been reported. Include your Windows version, Lychee version, steps to reproduce, expected behavior, actual behavior, and relevant logs or screenshots. Please do not include private or sensitive information.

## Pull requests

1. Create a focused branch from `main`.
2. Keep changes small and explain the motivation.
3. Preserve the existing coding style and user-facing behavior unless the change is intentional.
4. Build the project successfully before submitting the PR.
5. Describe what changed, how it was tested, and any limitations.

For changes involving the bundled PresentMon tool, preserve its accompanying license and attribution notices.

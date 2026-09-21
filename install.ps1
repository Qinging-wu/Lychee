# Lychee one-click installer for Windows
#
# PowerShell:
#   irm https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 | iex
#
# cmd:
#   curl -fsSL https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 -o "%TEMP%\lychee-install.ps1" && powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\lychee-install.ps1"

function Install-Lychee {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    if ($PSVersionTable.PSVersion.Major -lt 5) {
        Write-Host 'ERROR: PowerShell 5.1 or later is required.' -ForegroundColor Red
        return
    }

    $repo    = 'Qinging-wu/Lychee'
    $headers = @{ 'User-Agent' = 'lychee-installer' }
    $dest    = Join-Path $env:LOCALAPPDATA 'Lychee'

    Write-Host ''
    Write-Host 'Installing Lychee ...' -ForegroundColor Cyan

    # 1. Resolve the latest release via GitHub API
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
    }
    catch {
        Write-Host "ERROR: cannot reach GitHub API ($($_.Exception.Message))" -ForegroundColor Red
        Write-Host 'Check your network, or download manually from https://github.com/Qinging-wu/Lychee/releases'
        return
    }

    $asset = @($release.assets | Where-Object { $_.name -like '*.zip' }) | Select-Object -First 1
    if (-not $asset) {
        Write-Host 'ERROR: no .zip asset found in the latest release.' -ForegroundColor Red
        return
    }
    Write-Host "Latest release: $($release.tag_name)"

    # 2. Download
    $tmpZip = Join-Path $env:TEMP "Lychee-$([guid]::NewGuid().ToString('N')).zip"
    $tmpDir = "$tmpZip.extract"
    try {
        Write-Host "Downloading $($asset.name) ..."
        Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $tmpZip -UseBasicParsing
    }
    catch {
        Write-Host "ERROR: download failed ($($_.Exception.Message))" -ForegroundColor Red
        return
    }

    # 3. Extract and locate Lychee.exe (handles both flat and nested zip layouts)
    try { Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force }
    catch {
        Write-Host "ERROR: extraction failed ($($_.Exception.Message))" -ForegroundColor Red
        return
    }
    $exe = Get-ChildItem -Path $tmpDir -Recurse -Filter 'Lychee.exe' | Select-Object -First 1
    if (-not $exe) {
        Write-Host 'ERROR: Lychee.exe not found inside the archive.' -ForegroundColor Red
        return
    }

    # 4. Stop a running instance, then install
    if (Get-Process -Name 'Lychee' -ErrorAction SilentlyContinue) {
        Write-Host 'Stopping the running Lychee ...'
        Stop-Process -Name 'Lychee' -Force
        Start-Sleep -Milliseconds 500
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path (Join-Path $exe.Directory.FullName '*') -Destination $dest -Recurse -Force

    # 5. Start Menu shortcut
    try {
        $wsh = New-Object -ComObject WScript.Shell
        $lnk = $wsh.CreateShortcut((Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Lychee.lnk'))
        $lnk.TargetPath       = Join-Path $dest 'Lychee.exe'
        $lnk.WorkingDirectory = $dest
        $lnk.IconLocation     = (Join-Path $dest 'Lychee.exe') + ',0'
        $lnk.Save()
    }
    catch { }

    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "Installed to $dest" -ForegroundColor Green
    Write-Host "Release: $($release.tag_name)  |  Start Menu shortcut created."

    $answer = Read-Host 'Launch Lychee now? [Y/n]'
    if ($answer -notmatch '^[nN]') { Start-Process (Join-Path $dest 'Lychee.exe') }
}

Install-Lychee

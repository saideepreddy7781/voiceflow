# Installs, updates or removes VoiceFlow on Windows for the current user. No administrator rights needed.
#
#   irm https://raw.githubusercontent.com/saideepreddy7781/voiceflow/main/scripts/install.ps1 | iex
#
# With options, run it as a script block:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/saideepreddy7781/voiceflow/main/scripts/install.ps1))) -Version 0.2.0
#   ... -Uninstall            remove VoiceFlow, keep settings and history
#   ... -Uninstall -Purge     also delete settings, history, logs and the saved Groq key (asks first unless -Yes)
#   ... -NoLaunch             don't start VoiceFlow afterwards
#   ... -Arch arm64           pick the download yourself instead of detecting it
# Or set $env:VOICEFLOW_VERSION before `irm ... | iex`.
#
# It installs to %LOCALAPPDATA%\Programs\VoiceFlow, adds a Start menu shortcut and an entry in
# Settings > Apps > Installed apps, and checks the download against the release's SHA256SUMS.txt before
# anything is replaced. When the gh command is installed and signed in, it also checks the GitHub build
# attestation.
#
# Test hooks: -Source <folder> installs from a folder holding VoiceFlow-<version>-Windows-<arch>.exe and
# SHA256SUMS.txt instead of a GitHub release, and -InstallDir <folder> installs somewhere else.
#
# Everything runs inside Invoke-VoiceFlowSetup, called on the last line. PowerShell parses the whole text
# before running it, so a download that is cut short fails to parse and runs nothing.

param(
    [string]$Version = $env:VOICEFLOW_VERSION,
    [string]$Arch = '',
    [string]$Source = '',
    [string]$InstallDir = '',
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$Yes
)

function Invoke-VoiceFlowSetup {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'An installer talks to the person at the console.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Running the installer is the confirmation; -Purge asks again.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Nested functions read the parameters through PowerShell scoping.')]
    param(
        [string]$Version = '',
        [string]$Arch = '',
        [string]$Source = '',
        [string]$InstallDir = '',
        [switch]$NoLaunch,
        [switch]$Uninstall,
        [switch]$Purge,
        [switch]$Yes
    )

    # Scoped to this function, so the caller's session keeps its own preferences.
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 downloads far slower with a progress bar
    Set-StrictMode -Version 2.0

    $Repo = 'saideepreddy7781/voiceflow'
    $Publisher = 'Saideep Reddy'
    $MinVersion = [version]'0.1.0'
    $UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VoiceFlow'
    $RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $SelfText = $MyInvocation.MyCommand.ScriptBlock.ToString()

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Environment]::OSVersion.Version.Major -lt 10) {
        throw 'This installer is for Windows 10 and 11. On a Mac, see https://github.com/saideepreddy7781/voiceflow#macos'
    }

    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\VoiceFlow' }
    $Exe = Join-Path $InstallDir 'VoiceFlow.exe'
    $Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'VoiceFlow.lnk'
    $NoLaunch = $NoLaunch -or ($env:VOICEFLOW_NO_LAUNCH -eq '1')

    function Say([string]$Text) { Write-Host $Text }

    # Runs a program quietly and returns its exit code. Windows PowerShell 5.1 turns redirected stderr into
    # terminating errors when $ErrorActionPreference is Stop, so this runs with Continue.
    function Invoke-Quiet([string]$FilePath, [string[]]$Arguments) {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments *> $null
        return $LASTEXITCODE
    }

    function Assert-SecureUrl([string]$Url) {
        if (-not $Url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing a non-HTTPS address: $Url" }
    }

    function Get-Download([string]$Url, [string]$OutFile) {
        Assert-SecureUrl $Url
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -MaximumRedirection 5 `
            -Headers @{ 'User-Agent' = 'VoiceFlow-installer' }
    }

    function Get-LatestVersion {
        $tag = $null
        try {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing `
                -Headers @{ 'User-Agent' = 'VoiceFlow-installer'; 'Accept' = 'application/vnd.github+json' }
            $tag = [string]$release.tag_name
        } catch {
            # The API allows 60 requests an hour without a token. The releases page redirect has no such limit.
            $response = Invoke-WebRequest -Uri "https://github.com/$Repo/releases/latest" -UseBasicParsing `
                -Headers @{ 'User-Agent' = 'VoiceFlow-installer' }
            $final = $null
            if ($response.BaseResponse.PSObject.Properties['ResponseUri']) { $final = $response.BaseResponse.ResponseUri }
            elseif ($response.BaseResponse.PSObject.Properties['RequestMessage']) { $final = $response.BaseResponse.RequestMessage.RequestUri }
            if ($final -and ([string]$final) -match '/releases/tag/(v[^/?#]+)$') { $tag = $Matches[1] }
        }
        if (-not $tag) { throw 'Could not find the latest release. Check your internet connection, or pass -Version X.Y.Z.' }
        return $tag.TrimStart('v')
    }

    function Get-NativeArch {
        # The registry holds the machine's own architecture even when this PowerShell runs under x64 emulation on Arm.
        $value = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
        switch ($value) {
            'AMD64' { return 'x64' }
            'ARM64' { return 'arm64' }
            default { throw "VoiceFlow needs 64-bit Windows on an x64 or Arm processor. This PC reports $value." }
        }
    }

    function Get-ExpectedHash([string]$SumsFile, [string]$FileName) {
        $found = @()
        foreach ($line in Get-Content -LiteralPath $SumsFile) {
            $parts = $line.Trim() -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $FileName) { $found += $parts[0].ToLowerInvariant() }
        }
        if ($found.Count -ne 1 -or $found[0] -notmatch '^[0-9a-f]{64}$') { throw "SHA256SUMS.txt has no single checksum for $FileName." }
        return $found[0]
    }

    function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

    function Test-MarkOfTheWeb([string]$Path) {
        return [bool](Get-Item -LiteralPath $Path -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue)
    }

    function Test-Attestation([string]$Path) {
        $gh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $gh) { Say '  Build attestation: skipped, because the gh command is not installed.'; return }
        if ((Invoke-Quiet $gh.Path @('auth', 'status')) -ne 0) { Say '  Build attestation: skipped, because gh is not signed in (gh auth login).'; return }
        $verify = @('attestation', 'verify', $Path, '--repo', $Repo, '--signer-workflow', "$Repo/.github/workflows/release.yml")
        if ((Invoke-Quiet $gh.Path $verify) -ne 0) { throw "gh could not verify the build attestation for $(Split-Path $Path -Leaf). Nothing was installed." }
        Say "  Build attestation: verified, built by this repository's release workflow."
    }

    # VoiceFlow copies in this sign-in session, identified by the publisher in the file's version information.
    function Get-VoiceFlowProcess {
        $session = (Get-Process -Id $PID).SessionId
        foreach ($p in @(Get-Process -Name VoiceFlow -ErrorAction SilentlyContinue)) {
            if ($p.SessionId -ne $session) { continue }
            $path = $null
            try { $path = $p.Path } catch { $path = $null }
            if (-not $path) { throw 'VoiceFlow is running as administrator, so this installer cannot close it. Quit it from the tray icon, then run the installer again.' }
            $info = (Get-Item -LiteralPath $path).VersionInfo
            if ($info.ProductName -eq 'VoiceFlow' -and $info.CompanyName -eq $Publisher) { $p }
        }
    }

    # VoiceFlow has no quit command yet, so close its window first and end the process if it is still there.
    function Stop-VoiceFlow {
        $running = @(Get-VoiceFlowProcess)
        if ($running.Count -eq 0) { return }
        $paths = @($running | ForEach-Object { $_.Path })
        foreach ($p in $running) {
            Say "  Closing VoiceFlow running from $($p.Path)"
            try { [void]$p.CloseMainWindow() } catch { Write-Verbose 'No window to close.' }
        }
        foreach ($p in $running) {
            if (-not $p.WaitForExit(3000)) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                if (-not $p.WaitForExit(10000)) { throw 'VoiceFlow did not close. Quit it from the tray icon, then run the installer again.' }
            }
        }
        foreach ($path in $paths) {
            if (-not [string]::Equals($path, $Exe, [StringComparison]::OrdinalIgnoreCase)) {
                Say "  The copy at $path is no longer needed. You can delete it."
            }
        }
    }

    function Remove-Shortcut {
        if (Test-Path -LiteralPath $Shortcut) { Remove-Item -LiteralPath $Shortcut -Force }
    }

    function Invoke-Uninstall {
        Stop-VoiceFlow
        Remove-Shortcut
        if (Test-Path -LiteralPath $UninstallKey) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
        # "Start with Windows" points at the exe; drop it only when it points at this copy.
        $run = (Get-ItemProperty -LiteralPath $RunKey -Name VoiceFlow -ErrorAction SilentlyContinue)
        if ($run -and ([string]$run.VoiceFlow).IndexOf($Exe, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Remove-ItemProperty -LiteralPath $RunKey -Name VoiceFlow -Force
        }
        if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
        # The single-file exe unpacks its native libraries here on first launch.
        $cache = Join-Path $env:TEMP '.net\VoiceFlow'
        if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force -ErrorAction SilentlyContinue }
        Say "Removed VoiceFlow from $InstallDir."

        $data = Join-Path $env:APPDATA 'VoiceFlow'
        $logs = Join-Path $env:LOCALAPPDATA 'VoiceFlow'
        if (-not $Purge) {
            if (Test-Path -LiteralPath $data) { Say "Your settings and history are still in $data. Run with -Uninstall -Purge to delete them." }
            return
        }
        if (-not $Yes) {
            $answer = Read-Host 'Delete VoiceFlow settings, history, dictionary, snippets, meetings, logs and the saved Groq key? [y/N]'
            if ($answer -notmatch '^(y|yes)$') { Say 'Kept your data.'; return }
        }
        foreach ($folder in @($data, $logs)) {
            if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        }
        foreach ($target in @('VoiceFlow/groq', 'VoiceFlow/gemini')) { [void](Invoke-Quiet 'cmdkey.exe' @("/delete:$target")) }
        Say 'Deleted VoiceFlow data. Meeting notes you saved to a folder of your own are kept.'
    }

    function Install-VoiceFlowExe([string]$Downloaded, [string]$Expected, [string]$InstalledVersion) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        $staged = "$Exe.new"
        $backup = "$Exe.old"
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
        Copy-Item -LiteralPath $Downloaded -Destination $staged -Force
        if ((Get-Sha256 $staged) -ne $Expected) { Remove-Item -LiteralPath $staged -Force; throw 'The copied file does not match its checksum. Nothing was installed.' }
        # Downloads made by PowerShell normally carry no Mark of the Web. If something added one, this file has
        # already matched the release checksum, which is the check SmartScreen's prompt stands in for.
        if (Test-MarkOfTheWeb $staged) {
            Unblock-File -LiteralPath $staged
            Say '  Removed the Mark of the Web from the verified copy.'
        }

        $previous = $null
        if (Test-Path -LiteralPath $Exe) { $previous = (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion }
        Stop-VoiceFlow
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $Exe) { Move-Item -LiteralPath $Exe -Destination $backup -Force }
        try {
            Move-Item -LiteralPath $staged -Destination $Exe -Force
        } catch {
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $Exe -Force }
            throw
        }
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue

        # The uninstaller is this same script, so Settings > Apps can remove VoiceFlow without a download.
        $uninstaller = Join-Path $InstallDir 'uninstall.ps1'
        $quotedDir = $InstallDir.Replace("'", "''")
        $body = "# Removes VoiceFlow. Written by the VoiceFlow installer.`r`nfunction Invoke-VoiceFlowSetup {$SelfText}`r`nInvoke-VoiceFlowSetup -Uninstall -InstallDir '$quotedDir'`r`n"
        [IO.File]::WriteAllText($uninstaller, $body, (New-Object Text.UTF8Encoding($true)))

        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($Shortcut)
        $link.TargetPath = $Exe
        $link.WorkingDirectory = $InstallDir
        $link.IconLocation = "$Exe,0"
        $link.Description = 'Voice dictation: hold a hotkey, speak, and clean text is typed where you are.'
        $link.Save()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $command = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""
        New-Item -Path $UninstallKey -Force | Out-Null
        $sizeKb = [int][Math]::Ceiling((Get-Item -LiteralPath $Exe).Length / 1KB)
        $values = [ordered]@{
            DisplayName          = 'VoiceFlow'
            DisplayVersion       = $InstalledVersion
            Publisher            = $Publisher
            DisplayIcon          = "$Exe,0"
            InstallLocation      = $InstallDir
            UninstallString      = $command
            QuietUninstallString = $command
            URLInfoAbout         = "https://github.com/$Repo"
            HelpLink             = "https://github.com/$Repo/issues"
            InstallDate          = (Get-Date -Format 'yyyyMMdd')
        }
        foreach ($name in $values.Keys) { New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null }
        foreach ($name in 'NoModify', 'NoRepair') { New-ItemProperty -Path $UninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
        New-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value $sizeKb -PropertyType DWord -Force | Out-Null

        # Keep "Start with Windows" working when it pointed at an older copy somewhere else.
        $run = (Get-ItemProperty -LiteralPath $RunKey -Name VoiceFlow -ErrorAction SilentlyContinue)
        if ($run) { Set-ItemProperty -LiteralPath $RunKey -Name VoiceFlow -Value "`"$Exe`" --background" }

        return $previous
    }

    if ($Purge -and -not $Uninstall) { throw '-Purge only goes with -Uninstall.' }

    if ($Uninstall) { Invoke-Uninstall; return }

    $oldProtocols = [Net.ServicePointManager]::SecurityProtocol
    $work = Join-Path ([IO.Path]::GetTempPath()) ('voiceflow-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        # Windows PowerShell 5.1 on older Windows 10 may not offer TLS 1.2 by default. GitHub requires it.
        [Net.ServicePointManager]::SecurityProtocol = $oldProtocols -bor [Net.SecurityProtocolType]::Tls12
        New-Item -ItemType Directory -Path $work | Out-Null

        if (-not $Arch) { $Arch = Get-NativeArch }
        if ($Arch -notin @('x64', 'arm64')) { throw "-Arch must be x64 or arm64, not $Arch." }

        if ($Source) {
            $Source = (Resolve-Path -LiteralPath $Source).Path
            if (-not $Version) {
                $names = @(Get-ChildItem -LiteralPath $Source -Filter "VoiceFlow-*-Windows-$Arch.exe" | ForEach-Object Name)
                if ($names.Count -ne 1) { throw "Expected one VoiceFlow-<version>-Windows-$Arch.exe in $Source." }
                $Version = $names[0] -replace '^VoiceFlow-(.+)-Windows-.+\.exe$', '$1'
            }
        } elseif (-not $Version) {
            $Version = Get-LatestVersion
        }
        $Version = $Version.TrimStart('v')
        if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "`"$Version`" is not a version number like 0.2.0." }
        if ([version]($Version -replace '-.*$', '') -lt $MinVersion) { throw "VoiceFlow $Version is older than this installer supports ($MinVersion)." }

        $fileName = "VoiceFlow-$Version-Windows-$Arch.exe"
        $sums = Join-Path $work 'SHA256SUMS.txt'
        $download = Join-Path $work $fileName
        Say "Installing VoiceFlow $Version ($Arch)"
        if ($Source) {
            Copy-Item -LiteralPath (Join-Path $Source 'SHA256SUMS.txt') -Destination $sums
            Copy-Item -LiteralPath (Join-Path $Source $fileName) -Destination $download
        } else {
            $base = "https://github.com/$Repo/releases/download/v$Version"
            try { Get-Download "$base/SHA256SUMS.txt" $sums } catch { throw "Could not download SHA256SUMS.txt for $Version. Is $Version a released version? ($($_.Exception.Message))" }
            Say "  Downloading $fileName..."
            Get-Download "$base/$fileName" $download
        }
        if (Test-MarkOfTheWeb $download) { Say '  The download carries a Mark of the Web (Zone.Identifier).' }
        else { Say '  The download carries no Mark of the Web.' }

        $expected = Get-ExpectedHash $sums $fileName
        if ((Get-Sha256 $download) -ne $expected) { throw "The download's SHA-256 does not match SHA256SUMS.txt. Nothing was installed." }
        Say '  SHA-256: matches SHA256SUMS.txt.'
        if (-not $Source) { Test-Attestation $download }

        $previous = Install-VoiceFlowExe -Downloaded $download -Expected $expected -InstalledVersion $Version
        if (-not $previous) { Say "Installed VoiceFlow $Version in $InstallDir." }
        elseif ($previous -like "$Version*") { Say "Reinstalled VoiceFlow $Version in $InstallDir." }
        else { Say "Updated VoiceFlow from $($previous -replace '\+.*$', '') to $Version in $InstallDir." }
        Say 'Find it in the Start menu as VoiceFlow. Remove it in Settings > Apps > Installed apps.'

        if (-not $NoLaunch) {
            Start-Process -FilePath $Exe -WorkingDirectory $InstallDir
            Say 'VoiceFlow is starting. Look for its icon next to the clock, and paste your Groq key in the welcome window.'
        }
    } finally {
        [Net.ServicePointManager]::SecurityProtocol = $oldProtocols
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Invoke-VoiceFlowSetup -Version $Version -Arch $Arch -Source $Source -InstallDir $InstallDir `
    -NoLaunch:$NoLaunch -Uninstall:$Uninstall -Purge:$Purge -Yes:$Yes

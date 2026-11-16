# CI test for scripts/install.ps1 on a real Windows machine. It installs into the current user's profile, so run
# it on a CI runner, not on your own PC.
#
#   pwsh scripts/test-install-windows.ps1 -Exe out\win-x64\VoiceFlow.exe -Version 0.3.0 -Arch x64
#
# The exe is published as a fake release in a temporary folder and installed with -Source, first with Windows
# PowerShell 5.1 and then with PowerShell 7. Unless -SkipNetwork is given, it also installs the published
# 0.2.0 release the way a user would, through Invoke-Expression, to see what a real download looks like.
# One line per check: PASS, FAIL or INFO. Exit code 0 when nothing failed.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Arch,
    [switch]$SkipNetwork
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'install.ps1'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('vf-installer-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\VoiceFlow'
$installedExe = Join-Path $installDir 'VoiceFlow.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'VoiceFlow.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VoiceFlow'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$dataDir = Join-Path $env:APPDATA 'VoiceFlow'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$pwsh = (Get-Process -Id $PID).Path
$script:failures = 0

function Write-Result([string]$Status, [string]$Name, [string]$Detail = '') {
    if ($Status -eq 'FAIL') { $script:failures++ }
    Write-Output ("{0} {1}{2}" -f $Status, $Name, $(if ($Detail) { ": $Detail" } else { '' }))
}

function Test-Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        Write-Result -Status 'PASS' -Name $Name -Detail ([string]$detail)
    } catch {
        Write-Result -Status 'FAIL' -Name $Name -Detail $_.Exception.Message
    }
}

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function Invoke-Installer([string]$Shell, [string[]]$Arguments) {
    $log = Join-Path $work ('install-' + [Guid]::NewGuid().ToString('N') + '.log')
    & $Shell -NoProfile -ExecutionPolicy Bypass -File $installer @Arguments *> $log
    $code = $LASTEXITCODE
    Show-Log $log
    return $code
}

# Indented copy of a log, on the console only, so functions that return a value don't return the log too.
function Show-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path | ForEach-Object { Write-Host "    | $_" } }
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Test-Mark([string]$Path) { [bool](Get-Item -LiteralPath $Path -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue) }

function Build-FakeRelease([string]$Name, [byte[]]$Bytes, [string]$SumsFrom = '') {
    $dir = Join-Path $work $Name
    New-Item -ItemType Directory -Path $dir | Out-Null
    $file = Join-Path $dir "VoiceFlow-$Version-Windows-$Arch.exe"
    [IO.File]::WriteAllBytes($file, $Bytes)
    if ($SumsFrom) { Copy-Item -LiteralPath (Join-Path $SumsFrom 'SHA256SUMS.txt') -Destination $dir }
    else { "$(Get-Sha256 $file)  VoiceFlow-$Version-Windows-$Arch.exe" | Set-Content -LiteralPath (Join-Path $dir 'SHA256SUMS.txt') -Encoding ascii }
    return $dir
}

function Assert-Installed([string]$ExpectedHash, [string]$ExpectedVersion) {
    Test-Check 'exe installed with the expected SHA-256' { Assert-True ((Get-Sha256 $installedExe) -eq $ExpectedHash) 'hash differs'; $installedExe }
    Test-Check 'installed exe has no Mark of the Web' { Assert-True (-not (Test-Mark $installedExe)) 'Zone.Identifier present' }
    Test-Check 'Start menu shortcut points at the exe' {
        $shell = New-Object -ComObject WScript.Shell
        Assert-True (Test-Path -LiteralPath $shortcut) "missing $shortcut"
        $target = $shell.CreateShortcut($shortcut).TargetPath
        Assert-True ([string]::Equals($target, $installedExe, [StringComparison]::OrdinalIgnoreCase)) "target is $target"
        $shortcut
    }
    Test-Check 'Installed apps entry' {
        $k = Get-ItemProperty -LiteralPath $uninstallKey
        Assert-True ($k.DisplayName -eq 'VoiceFlow') "DisplayName $($k.DisplayName)"
        Assert-True ($k.DisplayVersion -eq $ExpectedVersion) "DisplayVersion $($k.DisplayVersion)"
        Assert-True ($k.Publisher -eq 'Saideep Reddy') "Publisher $($k.Publisher)"
        Assert-True ($k.DisplayIcon -eq "$installedExe,0") "DisplayIcon $($k.DisplayIcon)"
        Assert-True ($k.UninstallString -like '*uninstall.ps1*') "UninstallString $($k.UninstallString)"
        Assert-True ($k.EstimatedSize -gt 0) 'EstimatedSize missing'
        "$($k.DisplayName) $($k.DisplayVersion) by $($k.Publisher)"
    }
    Test-Check 'uninstall.ps1 parses' {
        $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $installDir 'uninstall.ps1'), [ref]$null, [ref]$errors)
        Assert-True ($errors.Count -eq 0) "$($errors.Count) parse errors"
    }
}

try {
    Write-Output "Installer test on $([Environment]::OSVersion.VersionString), $Arch, PowerShell $($PSVersionTable.PSVersion)"
    $good = [IO.File]::ReadAllBytes($Exe)
    $goodHash = Get-Sha256 $Exe

    Test-Check 'exe version information' {
        $info = (Get-Item -LiteralPath $Exe).VersionInfo
        Assert-True ($info.ProductName -eq 'VoiceFlow') "ProductName '$($info.ProductName)'"
        Assert-True ($info.CompanyName -eq 'Saideep Reddy') "CompanyName '$($info.CompanyName)'"
        Assert-True ($info.FileDescription -eq 'VoiceFlow') "FileDescription '$($info.FileDescription)'"
        Assert-True ($info.LegalCopyright -like 'Copyright*Saideep Reddy') "LegalCopyright '$($info.LegalCopyright)'"
        Assert-True ($info.ProductVersion -like "$Version*") "ProductVersion '$($info.ProductVersion)'"
        "$($info.ProductName) $($info.ProductVersion), $($info.CompanyName), $([Math]::Round($good.Length / 1MB, 1)) MB"
    }

    # A browser download carries a Mark of the Web. Give the fake release one, so the test covers the path where
    # the installer finds it and removes it after the checksum matches.
    $release = Build-FakeRelease 'release' $good
    Set-Content -LiteralPath (Join-Path $release "VoiceFlow-$Version-Windows-$Arch.exe") -Stream 'Zone.Identifier' -Value "[ZoneTransfer]`r`nZoneId=3"
    Test-Check 'fake release exe carries a Mark of the Web' { Assert-True (Test-Mark (Join-Path $release "VoiceFlow-$Version-Windows-$Arch.exe")) 'could not add Zone.Identifier' }

    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDir 'installer-test.txt') -Value 'user data'

    # 1. Fresh install with Windows PowerShell 5.1. No -Arch, so detection has to find the native architecture.
    $code = Invoke-Installer $windowsPowerShell @('-Source', $release, '-NoLaunch')
    Test-Check 'install with Windows PowerShell 5.1' { Assert-True ($code -eq 0) "exit code $code" }
    Assert-Installed $goodHash $Version
    Test-Check 'installed exe passes --self-test' {
        $out = Join-Path $work 'selftest.txt'
        $p = Start-Process -FilePath $installedExe -ArgumentList '--self-test', '--self-test-out', $out -Wait -PassThru
        Show-Log $out
        Assert-True ($p.ExitCode -eq 0) "exit code $($p.ExitCode)"
    }

    # 2. Reinstall with PowerShell 7 while VoiceFlow runs, with "Start with Windows" pointing at an old copy.
    Set-ItemProperty -LiteralPath $runKey -Name VoiceFlow -Value '"C:\Old\VoiceFlow.exe" --background'
    $running = Start-Process -FilePath $installedExe -PassThru
    Start-Sleep -Seconds 8
    $wasRunning = -not $running.HasExited
    $code = Invoke-Installer $pwsh @('-Source', $release, '-NoLaunch')
    Test-Check 'reinstall with PowerShell 7' { Assert-True ($code -eq 0) "exit code $code" }
    if ($wasRunning) {
        Test-Check 'running copy was closed before replacing' { Assert-True $running.HasExited 'the old process is still running' }
    } else {
        Write-Result -Status 'INFO' -Name 'running copy' -Detail "VoiceFlow exited by itself on this runner (code $($running.ExitCode)), so closing it was not tested"
    }
    Test-Check 'Start with Windows now points at the installed exe' {
        $value = (Get-ItemProperty -LiteralPath $runKey -Name VoiceFlow).VoiceFlow
        Assert-True ($value -eq "`"$installedExe`" --background") "Run value is $value"
    }
    Assert-Installed $goodHash $Version
    Get-Process -Name VoiceFlow -ErrorAction SilentlyContinue | Stop-Process -Force

    # 3. A changed byte must be refused and leave the installed copy alone.
    $bad = [byte[]]$good.Clone()
    $bad[$bad.Length - 100] = $bad[$bad.Length - 100] -bxor 0xFF
    $tampered = Build-FakeRelease 'tampered' $bad -SumsFrom $release
    $code = Invoke-Installer $pwsh @('-Source', $tampered, '-NoLaunch')
    Test-Check 'tampered download is refused' { Assert-True ($code -ne 0) 'installer succeeded' }
    Test-Check 'installed copy untouched after refusal' { Assert-True ((Get-Sha256 $installedExe) -eq $goodHash) 'hash changed' }

    # 4. Uninstall exactly the way Settings > Apps does: run the registered UninstallString.
    $command = (Get-ItemProperty -LiteralPath $uninstallKey).UninstallString
    $code = -1
    if ($command -match '^"([^"]+)"\s*(.*)$') {
        $out = Join-Path $work 'uninstall.out.log'
        $err = Join-Path $work 'uninstall.err.log'
        $p = Start-Process -FilePath $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $out -RedirectStandardError $err
        $code = $p.ExitCode
        Show-Log $out
        Show-Log $err
    }
    Test-Check 'registered uninstaller runs' { Assert-True ($code -eq 0) "exit code $code" }
    Test-Check 'uninstall removed the program, shortcut, entry and startup value' {
        Assert-True (-not (Test-Path -LiteralPath $installDir)) "$installDir still exists"
        Assert-True (-not (Test-Path -LiteralPath $shortcut)) 'shortcut still exists'
        Assert-True (-not (Test-Path -LiteralPath $uninstallKey)) 'uninstall key still exists'
        Assert-True (-not (Get-ItemProperty -LiteralPath $runKey -Name VoiceFlow -ErrorAction SilentlyContinue)) 'Run value still exists'
    }
    Test-Check 'uninstall kept user data' { Assert-True (Test-Path -LiteralPath (Join-Path $dataDir 'installer-test.txt')) 'data folder was removed' }

    # 5. Install again, then remove everything with -Purge.
    $code = Invoke-Installer $pwsh @('-Source', $release, '-NoLaunch')
    Test-Check 'install again' { Assert-True ($code -eq 0) "exit code $code" }
    $code = Invoke-Installer $pwsh @('-Uninstall', '-Purge', '-Yes')
    Test-Check 'uninstall with -Purge' {
        Assert-True ($code -eq 0) "exit code $code"
        Assert-True (-not (Test-Path -LiteralPath $dataDir)) 'data folder still exists'
        Assert-True (-not (Test-Path -LiteralPath $installDir)) 'install folder still exists'
    }

    if (-not $SkipNetwork) {
        # 6. What a real download looks like. Invoke-WebRequest saving a file: does it add a Zone.Identifier?
        foreach ($shell in @($windowsPowerShell, $pwsh)) {
            $file = Join-Path $work ('motw-' + [Guid]::NewGuid().ToString('N') + '.txt')
            & $shell -NoProfile -Command "`$ProgressPreference = 'SilentlyContinue'; Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/saideepreddy7781/voiceflow/releases/download/v0.2.0/SHA256SUMS.txt' -OutFile '$file'"
            Write-Result -Status 'INFO' -Name "Invoke-WebRequest -OutFile in $(Split-Path $shell -Leaf)" -Detail $(if (Test-Mark $file) { 'adds a Zone.Identifier' } else { 'adds no Zone.Identifier' })
        }

        # 7. The published 0.2.0 release, run the way the README says (irm | iex), with Windows PowerShell 5.1.
        $log = Join-Path $work 'network.log'
        $text = "`$env:VOICEFLOW_NO_LAUNCH = '1'; `$env:VOICEFLOW_VERSION = '0.2.0'; Get-Content -Raw -LiteralPath '$installer' | Invoke-Expression"
        & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -Command $text *> $log
        $code = $LASTEXITCODE
        Show-Log $log
        Test-Check 'published 0.2.0 installs through Invoke-Expression' {
            Assert-True ($code -eq 0) "exit code $code"
            Assert-True ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq '0.2.0') 'wrong version registered'
            Assert-True (-not (Test-Mark $installedExe)) 'Zone.Identifier present'
        }
        $code = Invoke-Installer $windowsPowerShell @('-Uninstall')
        Test-Check 'uninstall the published release' { Assert-True ($code -eq 0 -and -not (Test-Path -LiteralPath $installDir)) "exit code $code" }
    }
} finally {
    Get-Process -Name VoiceFlow -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Output "$script:failures check(s) failed."; exit 1 }
Write-Output 'All installer checks passed.'

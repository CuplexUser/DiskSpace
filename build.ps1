<#
.SYNOPSIS
    Build, test, publish and run DiskSpace.

.DESCRIPTION
    A thin, predictable wrapper over the dotnet CLI so every environment (a laptop, a fresh
    clone, CI) runs the same commands. Nothing here is required to work on the project; it
    exists so nobody has to remember the publish flags or the unelevated-run trick.

.PARAMETER Task
    Clean    Delete bin/, obj/ and artifacts/.
    Restore  Restore NuGet packages.
    Build    Build the solution. (default)
    Test     Run the test suite. Add -Coverage for a Cobertura report.
    Publish  Produce a single-file executable under artifacts/publish/.
    Installer Publish, then compile the Inno Setup installer to artifacts/installer/.
    Run      Build and launch the app. Uses the unelevated dev manifest unless -Elevated.
    Version  Show the resolved version, or set it with -SetVersion.
    All      Clean, build, test, publish.

.PARAMETER Configuration
    Debug (default) or Release. Publish defaults to Release when left unset.

.EXAMPLE
    ./build.ps1
    Builds the solution in Debug.

.EXAMPLE
    ./build.ps1 Test -Coverage
    Runs the tests and writes a coverage report under artifacts/coverage/.

.EXAMPLE
    ./build.ps1 Run
    Launches the app without a UAC prompt, for a normal debug session.

.EXAMPLE
    ./build.ps1 Publish
    Produces artifacts/publish/DiskSpace.exe, one file, no install step.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Clean', 'Restore', 'Build', 'Test', 'Publish', 'Installer', 'Run', 'Version', 'All')]
    [string]$Task = 'Build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration,

    [string]$Runtime = 'win-x64',

    # Bundles the .NET runtime into the executable. Larger, but runs on a machine with no
    # .NET installed at all.
    [switch]$SelfContained,

    # Publish and Run only: keep the requireAdministrator manifest. Off for Run, because a
    # UAC prompt on every F5 gets old; never off for a build you intend to hand to someone.
    [switch]$Elevated,

    [switch]$Coverage,

    [switch]$NoRestore,

    # Version task only: rewrite the version in Directory.Build.props, e.g. 0.2.0 or
    # 0.2.0-beta.1. Everything that carries a version reads it from there.
    [string]$SetVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$solution = Join-Path $root 'DiskSpace.slnx'
$appProject = Join-Path $root 'src/DiskSpace.App/DiskSpace.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$propsFile = Join-Path $root 'Directory.Build.props'

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Assert-Sdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw 'The .NET SDK was not found on PATH. Install .NET 10: https://dotnet.microsoft.com/download'
    }

    $version = (& dotnet --version).Trim()
    $major = [int]($version -split '\.')[0]
    if ($major -lt 10) {
        throw "This project targets net10.0-windows but the active SDK is $version. Install the .NET 10 SDK."
    }

    Write-Host "    .NET SDK $version" -ForegroundColor DarkGray
}

function Get-Configuration([string]$Default) {
    if ($Configuration) { return $Configuration }
    return $Default
}

function Invoke-Clean {
    Write-Step 'Cleaning'

    foreach ($directory in @('bin', 'obj')) {
        Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') `
            -Directory -Recurse -Filter $directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                Write-Host "    remove $($_.FullName.Substring($root.Length + 1))" -ForegroundColor DarkGray
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
    }

    if (Test-Path $artifacts) {
        Write-Host '    remove artifacts' -ForegroundColor DarkGray
        Remove-Item -LiteralPath $artifacts -Recurse -Force
    }
}

function Invoke-Restore {
    Write-Step 'Restoring'
    Invoke-Dotnet restore $solution
}

function Invoke-Build {
    $config = Get-Configuration 'Debug'
    Write-Step "Building ($config)"

    $arguments = @('build', $solution, '-c', $config)
    if ($NoRestore) { $arguments += '--no-restore' }
    Invoke-Dotnet @arguments
}

function Invoke-Test {
    $config = Get-Configuration 'Debug'
    Write-Step "Testing ($config)"

    $arguments = @('test', $solution, '-c', $config)
    if ($Coverage) {
        $results = Join-Path $artifacts 'coverage'
        $arguments += @('--collect:XPlat Code Coverage', '--results-directory', $results)
    }

    Invoke-Dotnet @arguments

    if ($Coverage) {
        $report = Get-ChildItem -Path (Join-Path $artifacts 'coverage') -Filter 'coverage.cobertura.xml' `
            -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($report) {
            Write-Host "    coverage: $($report.FullName)" -ForegroundColor DarkGray
        }
    }
}

function Invoke-Publish {
    $config = Get-Configuration 'Release'
    $output = Join-Path $artifacts 'publish'

    Write-Step "Publishing ($config, $Runtime)"

    if (-not $Elevated) {
        Write-Host '    manifest: requireAdministrator (the shipping default)' -ForegroundColor DarkGray
    }

    $arguments = @(
        'publish', $appProject,
        '-c', $config,
        '-r', $Runtime,
        '-o', $output,
        '-p:PublishSingleFile=true',
        "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded'
    )

    Invoke-Dotnet @arguments

    $exe = Join-Path $output 'DiskSpace.exe'
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host ''
        Write-Host "    $exe ($size MB)" -ForegroundColor Green
    }
}

function Get-VersionInfo {
    # Asks MSBuild rather than parsing XML, so this resolves the version exactly the way
    # the compiler does. Directory.Build.props is the only place a version is written down,
    # which is what keeps the assembly, the installer and Add/Remove Programs in agreement.
    $json = & dotnet msbuild $appProject -getProperty:Version -getProperty:VersionPrefix -v:quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve the version from $appProject."
    }

    $properties = ($json | Out-String | ConvertFrom-Json).Properties

    $revision = ''
    if (Test-Path (Join-Path $root '.git')) {
        $revision = (& git -C $root rev-parse --short HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) { $revision = '' }
    }

    return [pscustomobject]@{
        # 0.2.0-beta.1 when a suffix is set, otherwise the same as Numeric.
        Version  = $properties.Version
        # Three-part digits only. The Windows version resource accepts nothing else.
        Numeric  = $properties.VersionPrefix
        Revision = $revision
    }
}

function Set-AppVersion([string]$Value) {
    if ($Value -notmatch '^(\d+\.\d+\.\d+)(?:-([0-9A-Za-z.-]+))?$') {
        throw "'$Value' is not a valid version. Use 1.2.3 or 1.2.3-beta.1."
    }

    $prefix = $Matches[1]
    $suffix = if ($Matches.Count -gt 2) { $Matches[2] } else { '' }

    $content = Get-Content -LiteralPath $propsFile -Raw
    $content = $content -replace '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$prefix</VersionPrefix>"
    $content = $content -replace '<VersionSuffix>[^<]*</VersionSuffix>', "<VersionSuffix>$suffix</VersionSuffix>"

    Set-Content -LiteralPath $propsFile -Value $content -NoNewline -Encoding utf8

    Write-Host "    Directory.Build.props is now $Value" -ForegroundColor Green
}

function Invoke-Version {
    Write-Step 'Version'

    if ($SetVersion) {
        Set-AppVersion $SetVersion
    }

    $version = Get-VersionInfo

    Write-Host ''
    Write-Host "    Version   $($version.Version)"
    Write-Host "    Numeric   $($version.Numeric)"
    if ($version.Revision) {
        Write-Host "    Commit    $($version.Revision)"
    }
    Write-Host "    Installer DiskSpace-$($version.Version)-win-x64-setup.exe"

    if (-not $SetVersion) {
        Write-Host ''
        Write-Host '    Change it with: ./build.ps1 Version -SetVersion 0.2.0' -ForegroundColor DarkGray
    }
}

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw 'ISCC.exe was not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php, or put ISCC.exe on PATH.'
}

function Invoke-Installer {
    Invoke-Publish

    $version = Get-VersionInfo
    $compiler = Find-InnoCompiler
    $script = Join-Path $root 'installer/DiskSpace.iss'
    $output = Join-Path $artifacts 'installer'

    Write-Step "Building installer ($($version.Version))"
    Write-Host "    $compiler" -ForegroundColor DarkGray

    $arguments = @(
        "/DAppVersion=$($version.Version)",
        "/DAppVersionNumeric=$($version.Numeric)",
        "/DPayloadDir=$(Join-Path $artifacts 'publish')",
        "/DOutputDir=$output"
    )

    if ($SelfContained) { $arguments += '/DSelfContained' }
    $arguments += $script

    & $compiler @arguments | ForEach-Object {
        # ISCC is chatty about every file it compresses; keep the warnings and the result.
        if ($_ -match 'Warning|Error|Successful|Output') { Write-Host "    $_" -ForegroundColor DarkGray }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE."
    }

    $setup = Get-ChildItem -Path $output -Filter '*setup.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1

    if ($setup) {
        $size = [math]::Round($setup.Length / 1MB, 1)
        Write-Host ''
        Write-Host "    $($setup.FullName) ($size MB)" -ForegroundColor Green
    }
}

function Invoke-Run {
    $config = Get-Configuration 'Debug'
    Write-Step "Running ($config)"

    $arguments = @('build', $appProject, '-c', $config)
    if (-not $Elevated) {
        # app.dev.manifest drops requireAdministrator, so launching does not raise UAC.
        # Rules that reach machine-wide caches will report access denied, which is expected.
        $arguments += '-p:DevNoElevation=true'
        Write-Host '    unelevated (pass -Elevated for the real manifest)' -ForegroundColor DarkGray
    }

    Invoke-Dotnet @arguments

    $exe = Join-Path $root "src/DiskSpace.App/bin/$config/net10.0-windows/DiskSpace.exe"
    if (-not (Test-Path $exe)) {
        throw "Built, but $exe is missing."
    }

    if ($Elevated) {
        Start-Process -FilePath $exe -Verb RunAs
    }
    else {
        Start-Process -FilePath $exe
    }
}

Assert-Sdk

switch ($Task) {
    'Clean' { Invoke-Clean }
    'Restore' { Invoke-Restore }
    'Build' { Invoke-Build }
    'Test' { Invoke-Test }
    'Publish' { Invoke-Publish }
    'Installer' { Invoke-Installer }
    'Version' { Invoke-Version }
    'Run' { Invoke-Run }
    'All' {
        Invoke-Clean
        Invoke-Build
        Invoke-Test
        Invoke-Publish
    }
}

Write-Host ''
Write-Host "$Task complete." -ForegroundColor Green

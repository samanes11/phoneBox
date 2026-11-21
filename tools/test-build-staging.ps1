$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-build-test-' + [guid]::NewGuid().ToString('N'))
$scriptPath = Join-Path $fixture 'scripts/build.ps1'
$project = Join-Path $fixture 'agent/PhoneBox.Agent/PhoneBox.Agent.csproj'
$wrapper = Join-Path $fixture 'android/gradlew.bat'
$apk = Join-Path $fixture 'android/app/build/outputs/apk/release/app-release.apk'
foreach ($path in @($scriptPath, $project, $apk)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../scripts/build.ps1') -Destination $scriptPath
[IO.File]::WriteAllText($project, '<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>')
[IO.File]::WriteAllText($apk, 'fixture apk')
if ($env:OS -eq 'Windows_NT') {
    [IO.File]::WriteAllText($wrapper, "@exit /b 0`r`n")
} else {
    [IO.File]::WriteAllText($wrapper, "#!/bin/sh`nexit 0`n")
    chmod +x $wrapper
}

$fakeBuild = [pscustomobject]@{ Stages = [System.Collections.Generic.List[string]]::new(); Fail = $false }
function dotnet {
    $all = @($args)
    $out = $all[[array]::IndexOf($all, '-o') + 1]
    $fakeBuild.Stages.Add($out)
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $out 'fixture.txt'), 'agent artifact')
    $global:LASTEXITCODE = if ($fakeBuild.Fail) { 7 } else { 0 }
}

try {
    foreach ($failing in @($false, $true)) {
        $fakeBuild.Fail = $failing
        $message = ''
        try { & $scriptPath | Out-Null } catch { $message = $_.Exception.Message }
        if (-not $failing) {
            if ($message) { throw $message }
            $zips = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'dist') -Filter 'PhoneBoxAgent-*.zip')
            if ($zips.Count -ne 5) { throw 'Expected agent archives for five platforms' }
        } elseif ($message -notmatch '^publish failed for ') {
            throw "Expected a publish failure, got: $message"
        }
        foreach ($stagePath in $fakeBuild.Stages) {
            if (Test-Path -LiteralPath $stagePath) { throw "Publish stage was left behind: $stagePath" }
        }
    }
    'PASS: successful and failed local packaging both clean their publish stages'
} finally {
    foreach ($stagePath in $fakeBuild.Stages) {
        if ([IO.Directory]::Exists($stagePath)) { [IO.Directory]::Delete($stagePath, $true) }
    }
    [IO.Directory]::Delete($fixture, $true)
}

# A failing fake build above leaves $LASTEXITCODE non-zero; the runner's pwsh wrapper would report that as the result.
exit 0

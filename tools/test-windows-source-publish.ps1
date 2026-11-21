$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '../scripts/install.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-publish-test-' + [guid]::NewGuid().ToString('N'))
$oldLocalAppData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $fixture
$app = Join-Path $fixture 'PhoneBox/app'
$installed = Join-Path $app 'PhoneBoxAgent.exe'
New-Item -ItemType Directory -Path $app -Force | Out-Null
[IO.File]::WriteAllText($installed, 'running install')
$fakeState = [pscustomobject]@{ Stopped = $false; Output = ""; PublishExit = 0 }

function Get-Process { 'fixture agent' }
function Stop-Process {
    param([Parameter(ValueFromPipeline)]$InputObject, [switch]$Force)
    process { $fakeState.Stopped = $true }
}
function Start-Sleep { }
function dotnet {
    $all = @($args)
    $fakeState.Output = $all[[array]::IndexOf($all, '-o') + 1]
    New-Item -ItemType Directory -Path $fakeState.Output -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fakeState.Output 'partial.txt'), 'incomplete build')
    $global:LASTEXITCODE = $fakeState.PublishExit
}

try {
    foreach ($code in @(7, 0)) {
        $fakeState.PublishExit = $code
        $message = ''
        try { & $installer } catch { $message = $_.Exception.Message }
        if ($message -notmatch 'publish failed') { throw "Expected publish failure, got: $message" }
        if ($fakeState.Stopped) { throw 'A failed or incomplete publish stopped the running agent' }
        if ([IO.File]::ReadAllText($installed) -ne 'running install') { throw 'The existing binary changed' }
        if (Test-Path -LiteralPath (Join-Path $app 'partial.txt')) { throw 'Publish wrote into the installed agent folder' }
        if (Test-Path -LiteralPath $fakeState.Output) { throw 'The staged build was left behind' }
    }
    'PASS: failed and incomplete publishes leave the running installation alone'
} finally {
    [IO.Directory]::Delete($fixture, $true)
    $env:LOCALAPPDATA = $oldLocalAppData
}

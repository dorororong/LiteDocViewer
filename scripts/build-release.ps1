param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$iscc = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6이 없습니다. winget install --id JRSoftware.InnoSetup --exact 명령으로 설치하세요.'
}

Push-Location $projectRoot
try {
    & $dotnet publish '.\LiteDocViewer.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -o '.\artifacts\App'
    if ($LASTEXITCODE -ne 0) { throw '앱 publish가 실패했습니다.' }

    & $iscc "/DAppVersion=$Version" '.\installer\LiteDocViewer.iss'
    if ($LASTEXITCODE -ne 0) { throw '설치 프로그램 빌드가 실패했습니다.' }

    Write-Host ''
    Write-Host '배포 파일:'
    Get-Item '.\artifacts\App\LiteDocViewer.exe',
             '.\artifacts\Installer\LiteDocViewer-Setup-x64.exe' |
        Select-Object FullName, @{Name='SizeMB'; Expression={[math]::Round($_.Length / 1MB, 2)}} |
        Format-Table -AutoSize
}
finally {
    Pop-Location
}

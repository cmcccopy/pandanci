$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @(
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Data.dll",
    "/r:System.Drawing.dll",
    "/r:System.Web.Extensions.dll",
    "/r:System.Windows.Forms.dll",
    "/r:$root\System.Data.SQLite.DLL"
)
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /out:"$root\PandanciClone.exe" $refs "$PSScriptRoot\*.cs"
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler failed with exit code $LASTEXITCODE"
}
$sourceConfig = "$PSScriptRoot\PandanciClone.exe.config"
$targetConfig = "$root\PandanciClone.exe.config"
$copyConfig = -not (Test-Path -LiteralPath $targetConfig)
if (-not $copyConfig) {
    $copyConfig = (Get-FileHash -LiteralPath $sourceConfig -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $targetConfig -Algorithm SHA256).Hash
}
if ($copyConfig) {
    Copy-Item -LiteralPath $sourceConfig -Destination $targetConfig -Force
}
Write-Host "Built $root\PandanciClone.exe"

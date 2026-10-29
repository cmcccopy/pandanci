$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testWorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('PandanciTranslationTests-' + [Guid]::NewGuid().ToString('N'))
$testExe = Join-Path $testWorkDir 'TranslationServiceTests.exe'
$frameworkCsc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Path $testWorkDir | Out-Null
try {
    & $frameworkCsc /nologo /codepage:65001 /target:exe /platform:x64 "/out:$testExe" `
        /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Web.Extensions.dll `
        "$projectRoot\src\PandanciClone\TranslationService.cs" `
        "$projectRoot\src\PandanciClone\GoogleTranslationRequestPolicy.cs" `
        "$projectRoot\src\PandanciClone\GoogleTranslationCredentials.cs" `
        "$projectRoot\src\PandanciClone\GoogleLegacyWebTransport.cs" `
        "$PSScriptRoot\TranslationServiceTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Translation test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Translation tests failed.' }
}
finally {
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe -Force }
    Remove-Item -LiteralPath $testWorkDir
}

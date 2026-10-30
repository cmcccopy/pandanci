param([string]$Proxy = '', [string]$Python = 'python')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dictionaryDir = Join-Path $projectRoot 'dictionaries'
$downloadDir = Join-Path $dictionaryDir 'downloads'
New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dictionaryDir 'licenses') | Out-Null

function Get-DictionaryResource([string]$Url, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) {
        Write-Host "Using cached $Destination"
        return
    }
    $partialFile = $Destination + '.partial'
    $curlArgs = @('--disable', '--fail', '--location', '--retry', '2', '--connect-timeout', '20', '--max-time', '1200', '--output', $partialFile)
    if ($Proxy) { $curlArgs += @('--proxy', $Proxy) }
    $curlArgs += $Url
    & curl.exe @curlArgs
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    Move-Item -LiteralPath $partialFile -Destination $Destination
}

Get-DictionaryResource 'https://github.com/skywind3000/ECDICT/releases/download/1.0.28/ecdict-sqlite-28.zip' (Join-Path $downloadDir 'ecdict-sqlite-28.zip')
Get-DictionaryResource 'https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz' (Join-Path $downloadDir 'cedict_1_0_ts_utf-8_mdbg.txt.gz')
Get-DictionaryResource 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/LICENSE' (Join-Path $dictionaryDir 'licenses/ECDICT-MIT.txt')
Get-DictionaryResource 'https://creativecommons.org/licenses/by-sa/4.0/legalcode.txt' (Join-Path $dictionaryDir 'licenses/CC-BY-SA-4.0.txt')

& $Python "$PSScriptRoot\build_dictionary_index.py" --ecdict-archive (Join-Path $downloadDir 'ecdict-sqlite-28.zip') --cedict-gzip (Join-Path $downloadDir 'cedict_1_0_ts_utf-8_mdbg.txt.gz') --output-dir $dictionaryDir
if ($LASTEXITCODE -ne 0) { throw 'Dictionary installation failed. Close the app and retry; downloaded source archives have been retained.' }

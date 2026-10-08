param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$dotnetPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $dotnetPath build "$PSScriptRoot\src\STWin\STWin.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnetPath run --project "$PSScriptRoot\tests\STWin.Tests\STWin.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    & $dotnetPath publish "$PSScriptRoot\src\STWin\STWin.csproj" -c Release -r win-x64 --self-contained true -o "$PSScriptRoot\artifacts\STWin"
    exit $LASTEXITCODE
}

# Builds the standalone command-line tool: one self-contained skald.exe (no .NET install needed on the target machine).
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\cli'))
dotnet publish (Join-Path $PSScriptRoot '..\src\Skald.Cli\Skald.Cli.csproj') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $Output
if ($LASTEXITCODE -eq 0) { Write-Host "Built $(Join-Path $Output 'skald.exe')" }
exit $LASTEXITCODE

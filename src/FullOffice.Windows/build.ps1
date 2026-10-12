param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\app'))
$ErrorActionPreference='Stop'
$buildRoot=Join-Path ([IO.Path]::GetTempPath()) 'FullOffice-Windows-build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet publish (Join-Path $PSScriptRoot 'FullOffice.Windows.csproj') -c Release -r win-x64 --self-contained false -o $OutputDirectory -p:BaseIntermediateOutputPath="$buildRoot\obj\" -p:BaseOutputPath="$buildRoot\bin\" -p:RestorePackagesPath="$buildRoot\nuget\" -v minimal
if($LASTEXITCODE -ne 0){throw 'FullOffice build failed'}

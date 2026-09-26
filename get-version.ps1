$ErrorActionPreference = 'Stop'
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'RtlTerminal.csproj') -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Set Version in RtlTerminal.csproj to a three-part release version (major.minor.patch).'
}
$version

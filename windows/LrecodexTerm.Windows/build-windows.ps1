param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "LrecodexTerm.Windows.csproj"

dotnet build $project -c $Configuration

Write-Host "Build completed: $Configuration" -ForegroundColor Green

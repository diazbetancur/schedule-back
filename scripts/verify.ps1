[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release",
  [switch]$SkipRestore,
  [switch]$SkipTests,
  [switch]$RunMigrations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Name,
    [Parameter(Mandatory = $true)]
    [scriptblock]$Action
  )

  Write-Host "[verify-backend] $Name" -ForegroundColor Cyan
  & $Action
  Write-Host "[verify-backend] OK: $Name" -ForegroundColor Green
}

Push-Location $projectRoot

try {
  if (-not $SkipRestore) {
    Invoke-Step -Name "dotnet restore" -Action {
      dotnet restore .\Barbershop.sln
    }
  }

  Invoke-Step -Name "dotnet build" -Action {
    $buildArgs = @(".\Barbershop.sln", "-c", $Configuration)
    if ($SkipRestore) {
      $buildArgs += "--no-restore"
    }

    dotnet build @buildArgs
  }

  if (-not $SkipTests) {
    Invoke-Step -Name "dotnet test" -Action {
      $testArgs = @(
        ".\tests\Barbershop.Tests\Barbershop.Tests.csproj",
        "-c",
        $Configuration,
        "--no-build"
      )

      dotnet test @testArgs
    }
  }

  if ($RunMigrations) {
    Invoke-Step -Name "dotnet ef database update" -Action {
      dotnet ef database update --project .\src\Barbershop.Infrastructure\Barbershop.Infrastructure.csproj --startup-project .\src\Api.Barbershop\Api.Barbershop.csproj
    }
  } else {
    Write-Host "[verify-backend] SKIP: dotnet ef database update (use -RunMigrations to execute)" -ForegroundColor Yellow
  }

  Write-Host "[verify-backend] Completed." -ForegroundColor Green
} finally {
  Pop-Location
}

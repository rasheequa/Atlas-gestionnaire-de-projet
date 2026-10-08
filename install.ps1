<#
.SYNOPSIS
  Télécharge et installe Atlas (Windows 10/11, sans droits administrateur).
.EXAMPLE
  irm https://raw.githubusercontent.com/rasheequa/Atlas-gestionnaire-de-projet/main/install.ps1 | iex
.EXAMPLE
  .\install.ps1 -Silent -InstallDir "$env:LOCALAPPDATA\Atlas"
#>
param(
  [string]$InstallDir = "",
  [switch]$Silent
)

$ErrorActionPreference = "Stop"
$url = "https://github.com/rasheequa/Atlas-gestionnaire-de-projet/releases/latest/download/Atlas-Setup.exe"
$installer = Join-Path $env:TEMP "Atlas-Setup.exe"

Write-Host "Téléchargement d'Atlas..."
try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  # Proxy d'entreprise : réutilise les identifiants Windows
  [Net.WebRequest]::DefaultWebProxy.Credentials = [Net.CredentialCache]::DefaultCredentials
} catch { }

try {
  Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing
} catch {
  # Repli : curl.exe (livré avec Windows) gère l'authentification de proxy Windows
  Write-Host "Nouvel essai avec curl.exe..."
  curl.exe -sS --fail --proxy-negotiate -U ":" -L -o $installer $url
  if ($LASTEXITCODE -ne 0) { throw "Téléchargement impossible : $url" }
}

$arguments = @()
if ($Silent) { $arguments += "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }
if ($InstallDir -ne "") { $arguments += "/DIR=`"$InstallDir`"" }

Write-Host "Installation..."
$process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
Remove-Item $installer -Force -ErrorAction SilentlyContinue
if ($process.ExitCode -ne 0) { throw "L'installation a échoué (code $($process.ExitCode))." }
Write-Host "Atlas est installé. Utilisez le raccourci Atlas sur le Bureau."

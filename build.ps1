param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$SingleFile = $true
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectDir = Join-Path $ScriptDir "BobTools"
$ProjectFile = Join-Path $ProjectDir "BobTools.csproj"
$OutputDir = Join-Path $ScriptDir "publish"

Set-Location $ScriptDir

Write-Host "===========================" -ForegroundColor Cyan
Write-Host " Building BobTools         " -ForegroundColor Cyan
Write-Host "===========================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"
Write-Host "Self-Contained: $SelfContained"
Write-Host "Publish Single File: $SingleFile"
Write-Host ""

Write-Host "=> Cleaning previous build output..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}

$publishArgs = @(
    "publish",
    "`"$ProjectFile`"",
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", "`"$OutputDir`""
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
} else {
    $publishArgs += "--self-contained", "false"
}

if ($SingleFile) {
    $publishArgs += "-p:PublishSingleFile=true"
}

Write-Host "=> Running dotnet publish..." -ForegroundColor Yellow
Write-Host "Using command: dotnet $($publishArgs -join ' ')"
& dotnet $publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "=> Encrypting environment variables..." -ForegroundColor Yellow
$envPath = Join-Path $ScriptDir "BobTools\.env"
$envEncPath = Join-Path $OutputDir ".env.enc"

if (Test-Path $envPath) {
    try {
        $plainBytes = [System.Text.Encoding]::UTF8.GetBytes((Get-Content $envPath -Raw))
        
        $aes = [System.Security.Cryptography.Aes]::Create()
        $aes.Key = [System.Text.Encoding]::UTF8.GetBytes("BobToolsKey12345")
        $aes.IV = [System.Text.Encoding]::UTF8.GetBytes("BobToolsIV123456")
        
        $encryptor = $aes.CreateEncryptor($aes.Key, $aes.IV)
        $ms = New-Object System.IO.MemoryStream
        $cs = New-Object System.Security.Cryptography.CryptoStream $ms, $encryptor, "Write"
        $cs.Write($plainBytes, 0, $plainBytes.Length)
        $cs.FlushFinalBlock()
        $encryptedBytes = $ms.ToArray()
        
        $cs.Dispose()
        $ms.Dispose()
        $aes.Dispose()
        
        [System.IO.File]::WriteAllBytes($envEncPath, $encryptedBytes)
        Write-Host ".env successfully encrypted and saved to publish folder as .env.enc." -ForegroundColor DarkGray
    } catch {
        Write-Error "Failed to encrypt .env file: $_"
    }
} else {
    Write-Host "No .env file found to encrypt." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "===========================" -ForegroundColor Green
Write-Host " Build successful!         " -ForegroundColor Green
Write-Host "===========================" -ForegroundColor Green
Write-Host "The application has been built and published to:" -ForegroundColor Cyan
Write-Host $OutputDir -ForegroundColor Cyan
Write-Host ""

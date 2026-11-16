# Produit artifacts\Camembert-portable-win-x64.zip : Camembert.exe autonome (runtime .NET inclus), rien à installer.
# Usage : powershell -ExecutionPolicy Bypass -File build\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts'
$stage = Join-Path $out 'Camembert'
$zip = Join-Path $out 'Camembert-portable-win-x64.zip'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# Fichier unique pour le code managé ; les quelques bibliothèques natives (WPF, SkiaSharp) restent à côté,
# ce qui évite toute extraction dans un dossier temporaire au lancement.
dotnet publish "$root\src\Camembert.App\Camembert.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)." }

Remove-Item "$stage\*.pdb" # symboles de débogage de SkiaSharp (95 Mo), inutiles à l'utilisateur
Copy-Item "$root\LICENSE", "$root\THIRD-PARTY-NOTICES.txt", "$root\README.md" $stage
Compress-Archive -Path $stage -DestinationPath $zip
"Archive : $zip ({0:N1} Mo)" -f ((Get-Item $zip).Length / 1MB)

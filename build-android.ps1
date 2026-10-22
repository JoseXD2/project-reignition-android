
param(
    [string]$ToolsDir = "$env:USERPROFILE\godot-android-tools",
    [switch]$Install,
    [switch]$SkipImport
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # deixa o Invoke-WebRequest MUITO mais rapido
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$GodotVer     = "4.7.2"
$GodotTag     = "$GodotVer-stable"
$GodotName    = "Godot_v${GodotTag}_mono_win64"
$TemplateDir  = "$GodotVer.stable.mono"
$SettingsVer  = "4.7"
$BuildTools   = "36.1.0"
$Platform     = "android-36"

$RepoRoot   = $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot "Project"
$OutDir     = Join-Path $RepoRoot "build\android"
$ApkPath    = Join-Path $OutDir "ProjectReignition.apk"

if (-not (Test-Path (Join-Path $ProjectDir "project.godot"))) {
    throw "Nao achei Project\project.godot. Coloque este script na raiz do repo project-reignition."
}

New-Item -ItemType Directory -Force -Path $ToolsDir, $OutDir | Out-Null
$Dl = Join-Path $ToolsDir "_downloads"
New-Item -ItemType Directory -Force -Path $Dl | Out-Null

function WriteNoBom($path, $text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
}

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

function Download($url, $dest) {
    if (Test-Path $dest) { Write-Host "   ja baixado: $(Split-Path $dest -Leaf)"; return }
    Write-Host "   baixando $url"
    Invoke-WebRequest -Uri $url -OutFile "$dest.part" -UseBasicParsing
    Move-Item "$dest.part" $dest
}

function Unzip($zip, $dest) {
    if ($zip -notlike "*.zip") { $tmp = "$zip.zip"; Copy-Item $zip $tmp -Force; $zip = $tmp }
    Expand-Archive -Path $zip -DestinationPath $dest -Force
}

Step "Godot $GodotTag .NET"
$GodotDir = Join-Path $ToolsDir $GodotName
$GodotExe = Join-Path $GodotDir "${GodotName}_console.exe"
if (-not (Test-Path $GodotExe)) {
    $zip = Join-Path $Dl "$GodotName.zip"
    Download "https://github.com/godotengine/godot-builds/releases/download/$GodotTag/$GodotName.zip" $zip
    Unzip $zip $ToolsDir
}

New-Item -ItemType File -Force -Path (Join-Path $GodotDir "._sc_") | Out-Null
$EditorData = Join-Path $GodotDir "editor_data"
New-Item -ItemType Directory -Force -Path $EditorData | Out-Null

Step "Export templates"
$TplDest = Join-Path $EditorData "export_templates\$TemplateDir"
if (-not (Test-Path (Join-Path $TplDest "android_debug.apk"))) {
    $tpz = Join-Path $Dl "Godot_v${GodotTag}_mono_export_templates.tpz"
    Download "https://github.com/godotengine/godot-builds/releases/download/$GodotTag/Godot_v${GodotTag}_mono_export_templates.tpz" $tpz
    $tmp = Join-Path $Dl "tpl_extract"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Unzip $tpz $tmp
    New-Item -ItemType Directory -Force -Path $TplDest | Out-Null
    Copy-Item (Join-Path $tmp "templates\*") $TplDest -Recurse -Force
    Remove-Item $tmp -Recurse -Force
}

Step ".NET 10 SDK"
$DotnetDir = Join-Path $ToolsDir "dotnet"
if (-not (Test-Path (Join-Path $DotnetDir "dotnet.exe"))) {
    $inst = Join-Path $Dl "dotnet-install.ps1"
    Download "https://dot.net/v1/dotnet-install.ps1" $inst
    & powershell -NoProfile -ExecutionPolicy Bypass -File $inst -Channel 10.0 -InstallDir $DotnetDir
}
$env:DOTNET_ROOT = $DotnetDir
$env:PATH = "$DotnetDir;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Step "JDK 17 (Temurin)"
$JdkDir = Join-Path $ToolsDir "jdk17"
if (-not (Test-Path (Join-Path $JdkDir "bin\java.exe"))) {
    $zip = Join-Path $Dl "jdk17.zip"
    Download "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse" $zip
    $tmp = Join-Path $Dl "jdk_extract"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Unzip $zip $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    Move-Item $inner.FullName $JdkDir
    Remove-Item $tmp -Recurse -Force
}
$env:JAVA_HOME = $JdkDir
$env:PATH = "$JdkDir\bin;$env:PATH"

Step "Android SDK"
$SdkDir = Join-Path $ToolsDir "android-sdk"
$SdkMgr = Join-Path $SdkDir "cmdline-tools\latest\bin\sdkmanager.bat"
if (-not (Test-Path $SdkMgr)) {
    $zip = Join-Path $Dl "cmdline-tools.zip"
    Download "https://dl.google.com/android/repository/commandlinetools-win-13114758_latest.zip" $zip
    $tmp = Join-Path $Dl "clt_extract"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Unzip $zip $tmp
    New-Item -ItemType Directory -Force -Path (Join-Path $SdkDir "cmdline-tools") | Out-Null
    Move-Item (Join-Path $tmp "cmdline-tools") (Join-Path $SdkDir "cmdline-tools\latest")
    Remove-Item $tmp -Recurse -Force
}
$env:ANDROID_HOME = $SdkDir
$env:ANDROID_SDK_ROOT = $SdkDir
if (-not (Test-Path (Join-Path $SdkDir "build-tools\$BuildTools"))) {
    $yes = ("y`n" * 30)
    $yes | & $SdkMgr --sdk_root="$SdkDir" --licenses | Out-Null
    & $SdkMgr --sdk_root="$SdkDir" "platform-tools" "build-tools;$BuildTools" "platforms;$Platform"
}

Step "Keystore de debug"
$Keystore = Join-Path $ToolsDir "debug.keystore"
if (-not (Test-Path $Keystore)) {
    & "$JdkDir\bin\keytool.exe" -genkeypair -v -keystore $Keystore -storepass android `
        -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 `
        -dname "CN=Android Debug,O=Android,C=US" | Out-Null
}

Step "Configurando o editor"
function GdPath($p) { return ($p -replace '\\', '/') }
$settingsFile = Join-Path $EditorData "editor_settings-$SettingsVer.tres"
if (-not (Test-Path $settingsFile)) {
$txt = @"
[gd_resource type="EditorSettings" format=3]

[resource]
export/android/java_sdk_path = "$(GdPath $JdkDir)"
export/android/android_sdk_path = "$(GdPath $SdkDir)"
export/android/debug_keystore = "$(GdPath $Keystore)"
export/android/debug_keystore_user = "androiddebugkey"
export/android/debug_keystore_pass = "android"
"@
    WriteNoBom $settingsFile $txt
}

WriteNoBom $settingsFile ([System.IO.File]::ReadAllText($settingsFile).TrimStart([char]0xFEFF))

Step "export_presets.cfg"
$presets = Join-Path $ProjectDir "export_presets.cfg"
if ((Test-Path $presets) -and -not (Select-String -Path $presets -Pattern 'platform="Android"' -Quiet)) {
    Copy-Item $presets "$presets.bak" -Force
    Write-Host "   export_presets.cfg existente salvo como export_presets.cfg.bak"
}
if (-not (Test-Path $presets) -or -not (Select-String -Path $presets -Pattern 'platform="Android"' -Quiet)) {
$txt = @"
[preset.0]

name="Android"
platform="Android"
runnable=true
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path="../build/android/ProjectReignition.apk"
encryption_include_filters=""
encryption_exclude_filters=""
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.0.options]

custom_template/debug=""
custom_template/release=""
gradle_build/use_gradle_build=false
architectures/armeabi-v7a=false
architectures/arm64-v8a=true
architectures/x86=false
architectures/x86_64=false
version/code=1
version/name="1.0.9"
package/unique_name="org.fanremake.projectreignition"
package/name="Project Reignition"
package/signed=true
package/app_category=2
package/retain_data_on_uninstall=false
package/exclude_from_recents=false
package/show_in_android_tv=false
package/show_in_app_library=true
package/show_as_launcher_app=false
screen/immersive_mode=true
screen/support_small=true
screen/support_normal=true
screen/support_large=true
screen/support_xlarge=true
permissions/internet=true
permissions/access_network_state=true
"@
    WriteNoBom $presets $txt
}

$ptxt = [System.IO.File]::ReadAllText($presets).TrimStart([char]0xFEFF)
$ptxt = $ptxt -replace 'exclude_filter="addons/ffmpeg/\*"', 'exclude_filter=""'
WriteNoBom $presets $ptxt
if (-not (Test-Path (Join-Path $ProjectDir "addons\ffmpeg\android"))) {
    Write-Warning "addons\ffmpeg\android ainda nao existe: o plugin vai junto, mas os videos nao tocam no Android ate portarmos as libs."
}

if (-not $SkipImport) {
    Step "Importando assets (primeira vez pode levar 30-90 min, depende do PC)"
    & $GodotExe --headless --path "$ProjectDir" --import
    if ($LASTEXITCODE -ne 0) { Write-Warning "Import terminou com codigo $LASTEXITCODE (geralmente e so warning, continuando)" }
}

$ErrorActionPreference = "Continue"

Step "Exportando APK (log em build\android\export.log)"
if (Test-Path $ApkPath) { Remove-Item $ApkPath -Force }
& $GodotExe --headless --path "$ProjectDir" --export-debug "Android" "$ApkPath" 2>&1 | Tee-Object -FilePath (Join-Path $OutDir "export.log")

if (-not (Test-Path $ApkPath)) {
    Write-Host "`nO APK nao foi gerado. Mande o arquivo build\android\export.log" -ForegroundColor Red
    exit 1
}
$sizeMB = [math]::Round((Get-Item $ApkPath).Length / 1MB)
Write-Host "`nAPK gerado: $ApkPath ($sizeMB MB)" -ForegroundColor Green
if ($sizeMB -gt 4000) { Write-Warning "APK acima de ~4 GB: o Android pode recusar a instalacao." }

if ($Install) {
    Step "Instalando no celular via adb (ative Depuracao USB no aparelho)"
    $adb = Join-Path $SdkDir "platform-tools\adb.exe"
    & $adb devices
    & $adb install -r "$ApkPath"
    Write-Host "Para ver o log do jogo:  `"$adb`" logcat -s godot" -ForegroundColor Yellow
}

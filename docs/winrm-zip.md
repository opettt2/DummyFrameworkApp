Berikut adalah file Markdown (`DOCUMENTATION.md`) yang sudah dirapikan dan diformat secara bersih agar siap disimpan sebagai dokumentasi internal project kamu.

---

```markdown
# Panduan Simulasi & Integrasi Deployment .NET Framework via WinRM + ZIP

Dokumen ini berisi panduan teknis langkah demi langkah untuk mengonfigurasi, mensimulasikan, dan mengintegrasikan alur deployment aplikasi .NET Framework menggunakan metode **WinRM + ZIP Transfer** dari Runner (Laptop/Build Server) ke Target Server (Windows Home / Production).

---

## 1. Setup Server Target (Windows Home)

Jalankan PowerShell di Server Target sebagai **Administrator**:

### 1.1. Aktifkan WinRM & Firewall
```powershell
# Enable WinRM & PowerShell Remoting
Enable-PSRemoting -Force

# Buka firewall untuk port WinRM HTTP (5985)
New-NetFirewallRule -Name "WinRM-HTTP" -DisplayName "WinRM HTTP" -Enabled True -Direction Inbound -Protocol TCP -LocalPort 5985

# Izinkan semua host terhubung via WinRM (khusus lingkungan non-domain/testing)
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force

```

### 1.2. Buat Service Account Deployment

```powershell
# Buat akun local user khusus deployment
New-LocalUser -Name "svc-deploy" -Password (ConvertTo-SecureString "PasswordKuat123!" -AsPlainText -Force)

# Masukkan ke dalam grup Administrators
Add-LocalGroupMember -Group "Administrators" -Member "svc-deploy"

```

### 1.3. Siapkan Folder Destinasi

```powershell
# Folder penerima temporer dan folder deploy target
New-Item -ItemType Directory -Path "C:\temp" -Force
New-Item -ItemType Directory -Path "C:\inetpub\wwwroot\DummyFrameworkApp" -Force

```

---

## 2. Uji Koneksi WinRM dari Laptop (Runner)

Sebelum melakukan simulasi full deployment, pastikan laptop kamu dapat berkomunikasi dengan WinRM server target.

### 2.1. Konfigurasi WinRM Client (Di Laptop Runner)

Jalankan di PowerShell **Administrator** laptop:

```powershell
Enable-PSRemoting -SkipNetworkProfileCheck -Force
Set-Item WSMan:\localhost\Client\AllowUnencrypted -Value $true -Force
Set-Item WSMan:\localhost\Client\Auth\Basic -Value $true -Force
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force

```

### 2.2. Uji Sesi Interaktif

```powershell
# 1. Tes responsivitas port WinRM
Test-WSMan -ComputerName "192.168.1.50"

# 2. Ambil kredensial interaktif (User: svc-deploy)
$cred = Get-Credential -UserName "svc-deploy" -Message "Masukkan password svc-deploy"

# 3. Tes pembukaan sesi WinRM
$session = New-PSSession -ComputerName "192.168.1.50" -Credential $cred -Authentication Basic

# 4. Tes eksekusi perintah remote (Harus mengembalikan hostname target)
Invoke-Command -Session $session -ScriptBlock { hostname }

# 5. Tutup sesi
Remove-PSSession $session

```

---

## 3. Script Simulasi Deployment Lokal (`deploy-sim.ps1`)

Buat file script `deploy-sim.ps1` di mesin laptop/runner untuk menguji alur **Zip $\rightarrow$ Transfer $\rightarrow$ Extract**:

```powershell
param (
    [string]$TargetIP = "192.168.1.50",
    [string]$PublishDir = "D:\My Job\DummyFrameworkApp\_publish",
    [string]$DeployPath = "C:\inetpub\wwwroot\DummyFrameworkApp"
)

$ZipFile = "$env:TEMP\publish.zip"
$RemoteZip = "C:\temp\publish.zip"

Write-Host "1. Kompresi folder publish menjadi 1 file ZIP..." -ForegroundColor Cyan
if (Test-Path $ZipFile) { Remove-Item $ZipFile -Force }
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipFile -Force

Write-Host "2. Membuka Sesi WinRM ke $TargetIP..." -ForegroundColor Cyan
$cred = Get-Credential -UserName "svc-deploy" -Message "Masukkan Password Server Target"
$session = New-PSSession -ComputerName $TargetIP -Credential $cred -Authentication Basic

Write-Host "3. Mengirimkan file ZIP via WinRM (Copy-Item)..." -ForegroundColor Cyan
Copy-Item -Path $ZipFile -Destination $RemoteZip -ToSession $session -Force

Write-Host "4. Ekstrak & Deploy di Server Target..." -ForegroundColor Cyan
Invoke-Command -Session $session -ScriptBlock {
    param($RemoteZip, $DeployPath)

    if (-not (Test-Path $DeployPath)) { New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null }
    
    # Bersihkan folder target lama dan ekstrak zip baru
    Remove-Item "$DeployPath\*" -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $RemoteZip -DestinationPath $DeployPath -Force
    
    # Hapus file temporary di server target
    Remove-Item $RemoteZip -Force
    
    Write-Host "Deployment sukses di $DeployPath!"
} -ArgumentList $RemoteZip, $DeployPath

# Hapus sesi dan file zip lokal
Remove-PSSession $session
Remove-Item $ZipFile -Force

```

### Eksekusi Simulasi

```powershell
.\deploy-sim.ps1 -TargetIP "192.168.1.50"

```

#### Indikator Keberhasilan:

1. Tidak ada error berwarna merah pada PowerShell terminal.
2. File di server target `C:\inetpub\wwwroot\DummyFrameworkApp` ter-ekstrak dengan lengkap.
3. File `C:\temp\publish.zip` di server target langsung terhapus otomatis setelah ekstraksi.

---

## 4. Penerapan pada GitHub Actions (CI/CD)

### 4.1. Konfigurasi GitHub Repository Secrets

Buka **Repository Settings** $\rightarrow$ **Secrets and variables** $\rightarrow$ **Actions**, tambahkan secret berikut:

| Nama Secret | Nilai / Contoh |
| --- | --- |
| `DEPLOY_TARGET_HOST` | `192.168.1.50` *(IP Server Target)* |
| `DEPLOY_TARGET_USER` | `svc-deploy` |
| `DEPLOY_TARGET_PASSWORD` | `PasswordKuat123!` |

### 4.2. File Composite Action (`.github/action/dotnet-framework-build-deploy/action.yml`)

```yaml
name: 'Build and Deploy .NET Framework App via WinRM (ZIP)'
description: 'Builds .NET Framework app, compresses to ZIP, transfers via WinRM, and extracts on remote server'

inputs:
  solution-path:
    description: 'Path to .sln file'
    required: true
    default: 'DummyFrameworkApp.sln'
  build-configuration:
    description: 'Build configuration'
    required: true
    default: 'Release'
  publish-dir:
    description: 'Output publish directory'
    required: true
    default: '_publish'
  target-host:
    description: 'IP or Hostname of Target Server'
    required: true
  target-user:
    description: 'Username for Target Server WinRM'
    required: true
  target-password:
    description: 'Password for Target Server WinRM'
    required: true
  remote-deploy-path:
    description: 'Target path on remote server'
    required: true
    default: 'C:\inetpub\wwwroot\DummyFrameworkApp'

runs:
  using: 'composite'
  steps:
    - name: Setup MSBuild
      uses: microsoft/setup-msbuild@v2

    - name: Restore NuGet Packages
      shell: pwsh
      run: nuget restore ${{ inputs.solution-path }}

    - name: Build and Publish Application
      shell: pwsh
      run: |
        msbuild ${{ inputs.solution-path }} `
          /p:Configuration=${{ inputs.build-configuration }} `
          /p:DeployOnBuild=true `
          /p:WebPublishMethod=FileSystem `
          /p:publishUrl="${{ inputs.publish-dir }}"

    - name: Compress Publish Directory to ZIP
      shell: pwsh
      run: |
        $publishPath = Join-Path $env:GITHUB_WORKSPACE "${{ inputs.publish-dir }}"
        $zipPath = Join-Path$env:TEMP "publish.zip"
        
        if (Test-Path $zipPath) { Remove-Item$zipPath -Force }
        Compress-Archive -Path "$publishPath\*" -DestinationPath $zipPath -Force

    - name: Deploy via WinRM (Zipped Transfer)
      shell: pwsh
      run: |
        $targetHost = "${{ inputs.target-host }}".Trim()
        $zipPath = Join-Path$env:TEMP "publish.zip"
        $remoteZip = "C:\temp\publish.zip"

        if ([string]::IsNullOrWhiteSpace($targetHost)) {
            Write-Error "ERROR: DEPLOY_TARGET_HOST kosong!"
            exit 1
        }
        
        Set-Item WSMan:\localhost\Client\AllowUnencrypted -Value $true -Force -ErrorAction SilentlyContinue
        Set-Item WSMan:\localhost\Client\Auth\Basic -Value $true -Force -ErrorAction SilentlyContinue
        Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force -ErrorAction SilentlyContinue

        $pass = ConvertTo-SecureString "${{ inputs.target-password }}" -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential("${{ inputs.target-user }}", $pass)

        $session = New-PSSession -ComputerName $targetHost -Credential$cred -Authentication Basic

        # Ensure remote temp directory exists
        Invoke-Command -Session $session -ScriptBlock {
          if (-not (Test-Path "C:\temp")) { New-Item -ItemType Directory -Path "C:\temp" -Force | Out-Null }
        }

        Copy-Item -Path $zipPath -Destination $remoteZip -ToSession$session -Force

        Invoke-Command -Session $session -ScriptBlock {
            param($remoteZip,$deployPath)
            
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            
            if (-not (Test-Path $deployPath)) { New-Item -ItemType Directory -Path$deployPath -Force | Out-Null }
            Remove-Item "$deployPath\*" -Recurse -Force -ErrorAction SilentlyContinue
            Expand-Archive -Path $remoteZip -DestinationPath$deployPath -Force
            Remove-Item $remoteZip -Force
        } -ArgumentList $remoteZip, "${{ inputs.remote-deploy-path }}"

        Remove-PSSession $session
        Remove-Item $zipPath -Force

```

---

## 5. Pertimbangan & Peningkatan untuk Environment Produksi

Saat menerapkan metode ini di server produksi resmi kantor/klien, perhatikan 3 poin utama berikut:

1. **Penanganan IIS AppPool File Locking (`w3wp.exe`)**
Aplikasi yang berjalan di IIS akan mengunci DLL. Hentikan AppPool sebelum pembersihan folder target:
```powershell
Stop-WebAppPool -Name "DummyFrameworkApp" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
# ... Jalankan proses ekstrak zip ...
Start-WebAppPool -Name "DummyFrameworkApp"

```


2. **Mekanisme Rollback Otomatis**
Ubah nama folder lama menjadi `DummyFrameworkApp_backup` sebelum melakukan overwriting. Jika proses ekstrak gagal, kembalikan folder backup agar layanan tidak down.
3. **Enkripsi SSL (WinRM Port 5986 / HTTPS)**
Ganti penggunaan HTTP Unencrypted (Port 5985) dengan HTTPS (Port 5986) menggunakan sertifikat SSL/TLS internal pada environment produksi.

```

```
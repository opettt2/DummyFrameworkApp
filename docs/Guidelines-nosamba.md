# Guideline: Deploy ke Server Terpisah Tanpa SMB

Konteks: runner build (Windows) dan server target IIS adalah **mesin berbeda**, dan
kebijakan Protelindo **tidak mengizinkan SMB/file share**. Dokumen ini membahas 3
alternatif, lengkap dari setup awal sampai verifikasi deploy.

| Alternatif | Kompleksitas Setup | Butuh AD? | Cocok Untuk |
|---|---|---|---|
| **WinRM** | Sedang | Bisa domain atau local account | Environment yang sudah biasa pakai PowerShell Remoting |
| **OpenSSH** | Sedang | Tidak wajib (key-based) | Environment yang familiar SSH (mirip pola Linux) |
| **GitHub Artifact + 2 runner** | Rendah (tidak perlu network protocol baru) | Tidak perlu sama sekali | Paling "native" ke GitHub Actions, paling gampang di-approve security karena tidak ada koneksi baru dibuka |

**Rekomendasi cepat**: kalau tim security Protelindo juga ketat soal AD/domain auth
(sesuai cerita temanmu), coba **Alternatif 3 (GitHub Artifact)** dulu — ini
menghindari isu permission AD sama sekali karena tidak butuh autentikasi
cross-machine baru, cuma perlu runner ter-register di kedua mesin.

---

## Alternatif 1 — WinRM (PowerShell Remoting)

### 1.1 Setup di Server Target (dilakukan sekali)

Di server target, PowerShell **as Administrator**:

```powershell
# Enable WinRM & PowerShell Remoting
Enable-PSRemoting -Force

# Buka firewall untuk WinRM
# Port 5985 = HTTP, 5986 = HTTPS
New-NetFirewallRule -Name "WinRM-HTTP" -DisplayName "WinRM HTTP" -Enabled True -Direction Inbound -Protocol TCP -LocalPort 5985
```

**Kalau tidak domain-joined** (workgroup / local account), tambahkan runner host
ke TrustedHosts di server target:
```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "<IP-runner-atau-*>" -Force
```

**Untuk production, sangat disarankan pakai HTTPS** (port 5986) — butuh
sertifikat di server target. Kalau belum ada infra sertifikat internal, mulai
dari HTTP dulu untuk testing (`use-ssl: false` di action), tapi ini cuma cocok
untuk jaringan internal yang trusted.

### 1.2 Siapkan Kredensial

Karena AD bermasalah, **local account** khusus service deploy sering lebih
gampang: buat local user di server target khusus untuk ini, masukkan ke grup
**Administrators** (atau idealnya grup custom dengan hak IIS management saja,
lebih aman tapi lebih ribet setup-nya).

```powershell
New-LocalUser -Name "svc-deploy" -Password (ConvertTo-SecureString "PasswordKuat123!" -AsPlainText -Force)
Add-LocalGroupMember -Group "Administrators" -Member "svc-deploy"
```

### 1.3 Simpan Kredensial sebagai GitHub Secret

Repo → **Settings → Secrets and variables → Actions**:
- `DEPLOY_TARGET_HOST` → IP/hostname server target
- `DEPLOY_TARGET_USER` → `.\svc-deploy` (local) atau `DOMAIN\svc-deploy` (kalau domain)
- `DEPLOY_TARGET_PASSWORD` → password user tsb

### 1.4 Test Koneksi Manual Dulu (dari runner, sebelum lewat CI)

Login/RDP ke mesin runner, jalankan di PowerShell:
```powershell
$cred = Get-Credential   # masukkan svc-deploy & password
Test-WSMan -ComputerName <IP-server-target>
New-PSSession -ComputerName <IP-server-target> -Credential $cred
```
Kalau ini berhasil manual, berarti network & kredensial sudah benar — baru lanjut ke CI.

### 1.5 Pakai di Workflow

```yaml
jobs:
  build-and-deploy:
    runs-on: windows
    steps:
      - uses: actions/checkout@v4

      # Build tetap sama seperti sebelumnya (MSBuild), tapi cukup sampai
      # publish - JANGAN pakai composite action lama yang deploy-nya lokal.
      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2

      - name: Build & Publish
        shell: pwsh
        run: |
          msbuild DummyFrameworkApp.sln /p:Configuration=Release /p:DeployOnBuild=true /p:WebPublishMethod=FileSystem /p:publishUrl="_publish"

      - name: Deploy via WinRM
        uses: ./.github/action/dotnet-framework-deploy-winrm
        with:
          publish-dir: '_publish'
          target-host: ${{ secrets.DEPLOY_TARGET_HOST }}
          target-user: ${{ secrets.DEPLOY_TARGET_USER }}
          target-password: ${{ secrets.DEPLOY_TARGET_PASSWORD }}
          use-ssl: 'false'   # ganti 'true' setelah HTTPS listener siap
          remote-deploy-path: 'C:\inetpub\wwwroot\DummyFrameworkApp'
          iis-site-name: 'DummyFrameworkApp'
          iis-port: '8081'
```

### 1.6 Troubleshooting WinRM

| Gejala | Penyebab | Solusi |
|---|---|---|
| `WinRM cannot complete the operation` | WinRM belum enabled di target, atau firewall block | Ulangi step 1.1, cek `Test-WSMan` manual dulu |
| `Access is denied` | User bukan Administrator, atau bukan member grup yang di-trust WinRM | Cek step 1.2, atau `Set-Item WSMan:\localhost\Service\Auth\Basic -Value $true` kalau pakai Basic auth non-domain |
| `The WinRM client cannot process the request because the server name cannot be resolved` | TrustedHosts belum di-set (non-domain) | Ulangi bagian TrustedHosts di 1.1 |
| Copy-Item lambat sekali | WinRM memang tidak seefisien SMB untuk file besar | Kalau publish output besar, pertimbangkan compress jadi zip dulu sebelum `Copy-Item -ToSession`, extract di sisi remote |

---

## Alternatif 2 — OpenSSH

### 2.1 Setup di Server Target (dilakukan sekali)

PowerShell **as Administrator** di server target (Windows Server 2019+):
```powershell
# Install OpenSSH Server
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0

# Start & set auto-start
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic

# Buka firewall port 22
New-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -DisplayName "OpenSSH Server (sshd)" -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22
```

**Opsional tapi disarankan**: set default shell SSH ke PowerShell (supaya
command remote lebih konsisten):
```powershell
New-ItemProperty -Path "HKLM:\SOFTWARE\OpenSSH" -Name DefaultShell -Value "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force
```

### 2.2 Generate SSH Key Pair (di lokal/mesin manapun, bukan di server)

```powershell
ssh-keygen -t ed25519 -f deploy_key -C "github-actions-deploy" -N '""'
```
Ini hasilkan 2 file: `deploy_key` (private) dan `deploy_key.pub` (public).

### 2.3 Daftarkan Public Key di Server Target

**Kalau user SSH-nya member Administrators**, public key harus masuk ke file
khusus (bukan `authorized_keys` biasa di home folder):
```powershell
# Di server target, sebagai Administrator:
Add-Content -Path "$env:ProgramData\ssh\administrators_authorized_keys" -Value (Get-Content "path\ke\deploy_key.pub")

# Set permission yang benar - WAJIB, kalau tidak SSH akan reject key ini
icacls "$env:ProgramData\ssh\administrators_authorized_keys" /inheritance:r
icacls "$env:ProgramData\ssh\administrators_authorized_keys" /grant "Administrators:F" /grant "SYSTEM:F"
```

**Kalau user biasa (non-admin)**, lebih simpel, taruh di home folder user itu:
```powershell
# Login sebagai user tersebut, atau jalankan dengan runas
mkdir $env:USERPROFILE\.ssh -Force
Add-Content -Path "$env:USERPROFILE\.ssh\authorized_keys" -Value (Get-Content "path\ke\deploy_key.pub")
```

### 2.4 Simpan Private Key sebagai GitHub Secret

Repo → **Settings → Secrets and variables → Actions**:
- `DEPLOY_TARGET_HOST` → IP/hostname server target
- `DEPLOY_SSH_USER` → username SSH
- `DEPLOY_SSH_PRIVATE_KEY` → **isi lengkap** file `deploy_key` (private key), termasuk baris `-----BEGIN ... KEY-----` dan `-----END ... KEY-----`

### 2.5 Test Koneksi Manual Dulu

Dari mesin manapun yang punya private key-nya:
```bash
ssh -i deploy_key user@<IP-server-target> "hostname"
```
Kalau berhasil connect tanpa diminta password, konfigurasi sudah benar.

### 2.6 Pakai di Workflow

```yaml
jobs:
  build-and-deploy:
    runs-on: windows
    steps:
      - uses: actions/checkout@v4

      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2

      - name: Build & Publish
        shell: pwsh
        run: |
          msbuild DummyFrameworkApp.sln /p:Configuration=Release /p:DeployOnBuild=true /p:WebPublishMethod=FileSystem /p:publishUrl="_publish"

      - name: Deploy via SSH
        uses: ./.github/action/dotnet-framework-deploy-ssh
        with:
          publish-dir: '_publish'
          target-host: ${{ secrets.DEPLOY_TARGET_HOST }}
          ssh-user: ${{ secrets.DEPLOY_SSH_USER }}
          ssh-private-key: ${{ secrets.DEPLOY_SSH_PRIVATE_KEY }}
          remote-deploy-path: 'C:\inetpub\wwwroot\DummyFrameworkApp'
          iis-site-name: 'DummyFrameworkApp'
          iis-port: '8081'
```

### 2.7 Troubleshooting SSH

| Gejala | Penyebab | Solusi |
|---|---|---|
| `Permission denied (publickey)` | Public key belum terdaftar dengan benar, atau salah file (`authorized_keys` vs `administrators_authorized_keys`) | Cek ulang step 2.3, ingat user Administrator WAJIB pakai `administrators_authorized_keys` |
| `UNPROTECTED PRIVATE KEY FILE` | Permission file private key di runner terlalu terbuka | Action ini sudah handle lewat `icacls`, tapi kalau test manual di Windows, pastikan permission private key hanya untuk user kamu |
| `ssh: connect to host port 22: Connection refused` | Service sshd belum start, atau firewall block | Cek `Get-Service sshd`, ulangi step 2.1 |
| Command sukses tapi IIS tidak berubah | Default shell SSH bukan PowerShell (masih cmd.exe) | Set `DefaultShell` seperti di step 2.1 |

---

## Alternatif 3 — GitHub Actions Artifact + Runner Kedua

Ini **paling aman dari sisi security policy** karena tidak membuka protokol
remote-access baru sama sekali antar mesin — komunikasi cuma lewat GitHub
sebagai perantara (upload/download artifact via HTTPS ke github.com, yang
biasanya sudah diizinkan karena runner memang butuh akses itu untuk checkout
code).

### 3.1 Register Runner Kedua di Server Target

Sama seperti register runner Windows sebelumnya (lihat guideline pertama),
tapi kali ini **langsung di server target IIS**, bukan di mesin build.

Saat `config.cmd`, beri **label berbeda**, misalnya:
```
--labels windows-deploy-target
```
Sementara runner build tetap pakai label `windows` seperti sebelumnya.

Sekarang kamu punya 2 runner:
- `windows` → mesin build (punya MSBuild + Build Tools)
- `windows-deploy-target` → mesin IIS target (tidak perlu MSBuild sama sekali)

### 3.2 Pisahkan Workflow Jadi 2 Job

```yaml
name: .NET Framework Dummy App - Build & Deploy (Artifact-based)

on:
  push:
    branches: [ "main", "master" ]

permissions:
  contents: read

jobs:
  build:
    name: Build
    runs-on: windows   # runner di mesin build
    steps:
      - uses: actions/checkout@v4

      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2

      - name: Build & Publish
        shell: pwsh
        run: |
          msbuild DummyFrameworkApp.sln /p:Configuration=Release /p:DeployOnBuild=true /p:WebPublishMethod=FileSystem /p:publishUrl="_publish"

      # Upload hasil publish sebagai GitHub Actions artifact - disimpan
      # sementara di GitHub, bukan dikirim langsung ke server manapun.
      - name: Upload build artifact
        uses: actions/upload-artifact@v4
        with:
          name: webapp-publish
          path: _publish/
          retention-days: 1

  deploy:
    name: Deploy
    needs: build
    runs-on: windows-deploy-target   # runner di mesin IIS target
    steps:
      # Download artifact yang di-upload job "build" - ini yang
      # menggantikan robocopy/scp/WinRM sepenuhnya.
      - name: Download build artifact
        uses: actions/download-artifact@v4
        with:
          name: webapp-publish
          path: _publish

      - name: Deploy to IIS (local, same machine)
        shell: pwsh
        run: |
          Import-Module WebAdministration
          $siteName = "DummyFrameworkApp"
          $appPoolName = $siteName
          $port = 8081
          $deployPath = "C:\inetpub\wwwroot\DummyFrameworkApp"

          if (Test-Path "IIS:\AppPools\$appPoolName") {
            Stop-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
          } else {
            New-WebAppPool -Name $appPoolName
            Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name managedRuntimeVersion -Value "v4.0"
          }

          if (-not (Test-Path $deployPath)) { New-Item -ItemType Directory -Path $deployPath -Force | Out-Null }
          robocopy "_publish" "$deployPath" /MIR /NFL /NDL /NJH /NJS
          if ($LASTEXITCODE -ge 8) { Write-Error "Robocopy gagal"; exit 1 }

          if (-not (Test-Path "IIS:\Sites\$siteName")) {
            New-Website -Name $siteName -PhysicalPath $deployPath -ApplicationPool $appPoolName -Port $port
          } else {
            Set-ItemProperty "IIS:\Sites\$siteName" -Name physicalPath -Value $deployPath
          }

          Start-WebAppPool -Name $appPoolName
          Start-Website -Name $siteName

      - name: Smoke test
        shell: pwsh
        run: |
          $response = Invoke-WebRequest -Uri "http://localhost:8081/" -UseBasicParsing -TimeoutSec 10
          if ($response.StatusCode -ne 200) { Write-Error "Smoke test gagal"; exit 1 }
          Write-Host "Smoke test sukses."
```

Perhatikan: `robocopy` di sini **bukan** SMB — ini copy lokal disk-ke-disk di
dalam mesin yang sama (`windows-deploy-target` runner jalan langsung di server
IIS itu), jadi tidak melanggar policy "no SMB" sama sekali.

### 3.3 Troubleshooting Artifact-based

| Gejala | Penyebab | Solusi |
|---|---|---|
| Job `deploy` stuck di "Waiting for a runner" | Runner `windows-deploy-target` belum online/belum terdaftar | Cek **Settings → Actions → Runners**, pastikan status "Idle" |
| `download-artifact` gagal "not found" | Nama artifact di upload & download tidak sama, atau job `build` gagal | Cek `name:` di kedua step harus identik persis |
| Artifact kadaluarsa/kosong | `retention-days` terlalu pendek dibanding waktu antar job | Biasanya bukan masalah karena `needs: build` bikin job berurutan, tapi naikkan `retention-days` kalau perlu debug ulang run lama |

---

## Ringkasan Keputusan

- **Kalau boleh buka port baru (WinRM 5985/5986 atau SSH 22) antar mesin internal** → pilih salah satu dari Alternatif 1/2, SSH biasanya lebih familiar & key-based auth lebih gampang di-manage daripada password WinRM.
- **Kalau security policy juga ketat soal koneksi baru antar mesin, atau AD terus bermasalah** → **Alternatif 3** adalah yang paling minim friction, karena secara teknis tidak ada koneksi baru yang perlu di-approve — cuma perlu daftarkan 1 runner tambahan di server target.
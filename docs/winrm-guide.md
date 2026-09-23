Berikut adalah panduan singkat cara masuk, mengelola, dan menutup sesi WinRM di PowerShell.

---

### 1. Persiapan Kredensial

Simpan akun user dan password ke dalam variabel kredensial terlebih dahulu:

```powershell
$cred = Get-Credential -UserName "svc-deploy" -Message "Masukkan Password Server Target"

```

---

### 2. Sesi Interaktif (`Enter-PSSession` & `Exit-PSSession`)

Gunakan cara ini jika kamu ingin **masuk langsung ke terminal server target** untuk *debugging* atau mengecek folder secara manual (seperti SSH).

#### A. Masuk Sesi Remote

```powershell
Enter-PSSession -ComputerName "192.168.1.50" -Credential $cred -Authentication Basic

```

> *Tanda berhasil:* Prompt terminal akan berubah dari `PS C:\...>` menjadi `[192.168.1.50]: PS C:\Users\svc-deploy\Documents>`.

#### B. Keluar Sesi Remote

```powershell
Exit-PSSession

```

> Terminal akan kembali ke prompt laptop lokal kamu.

---

### 3. Sesi Persistent / Background (`New-PSSession` & `Remove-PSSession`)

Gunakan cara ini untuk **otomasi script**, transfer file (`Copy-Item`), atau mengeksekusi banyak perintah secara berurutan tanpa harus masuk-keluar terminal secara manual.

#### A. Buat dan Simpan Sesi ke Variabel

```powershell
$session = New-PSSession -ComputerName "192.168.1.50" -Credential $cred -Authentication Basic

```

#### B. Cek Sesi yang Sedang Aktif

```powershell
Get-PSSession

```

#### C. Jalankan Perintah di Dalam Sesi

```powershell
Invoke-Command -Session $session -ScriptBlock { Get-Service -Name "W3SVC" }

```

#### D. Kirim File Menggunakan Sesi

```powershell
Copy-Item -Path "C:\local\file.txt" -Destination "C:\temp\file.txt" -ToSession $session

```

#### E. Menutup dan Menghapus Sesi (Wajib untuk Clean Up)

```powershell
# Menutup sesi tertentu
Remove-PSSession $session

# Atau menghapus SELURUH sesi WinRM yang masih menggantung/aktif di memori
Get-PSSession | Remove-PSSession

```

---

### Summary Cheat Sheet

| Kebutuhan | Perintah PowerShell |
| --- | --- |
| **Masuk Terminal Target** | `Enter-PSSession -ComputerName "IP" -Credential $cred -Authentication Basic` |
| **Keluar Terminal Target** | `Exit-PSSession` |
| **Buat Sesi Background** | `$session = New-PSSession -ComputerName "IP" -Credential $cred -Authentication Basic` |
| **Cek Sesi Aktif** | `Get-PSSession` |
| **Hapus Sesi Spesifik** | `Remove-PSSession $session` |
| **Hapus Semua Sesi** | `Get-PSSession | Remove-PSSession` |
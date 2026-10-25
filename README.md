# kirimdong

**Kirim file antar PC Windows di Wi-Fi yang sama.** Buka kirimdong di dua PC, labelnya saling muncul,
seret file ke label tujuan, PC penerima menekan **Terima**. Tanpa akun, tanpa internet, tanpa kabel,
tanpa upload ke cloud.

Tampilannya dibuat seperti kardus paket: tiap PC adalah label pengiriman, tiap transfer adalah slip yang
mencatat kecepatan dan sisa waktunya.

**Download:** [rilis terbaru](https://github.com/farishiddiq/kirimdong/releases/latest) (`kirimdong-<versi>-win-x64.msi`)

## Screenshots

Laptop Rina melihat dua PC lain di Wi-Fi yang sama. Folder foto sudah terkirim, rekaman rapat 2,3 GB sedang masuk.

![kirimdong: dua label PC tujuan dan slip pengiriman yang sedang berjalan](promo/assets/kirimdong-main.png)

Paket masuk selalu ditanyakan dulu: siapa pengirimnya, isinya apa, berapa besar, dan disimpan ke mana.

![kirimdong: kartu "Ada paket untukmu dari PC Ruang Tamu" dengan tombol Terima dan Tolak](promo/assets/kirimdong-offer.png)

Belum ada PC lain yang terbuka:

![kirimdong: kardus kosong, menunggu PC lain di jaringan](promo/assets/kirimdong-empty.png)

## Fitur

- **Otomatis saling menemukan.** PC yang membuka kirimdong di jaringan yang sama muncul sebagai label dalam
  beberapa detik (broadcast UDP di jaringan lokal), dan hilang lagi saat app-nya ditutup.
- **File dan folder.** Seret ke label, atau pakai **Pilih file** / **Folder**. Isi folder dikirim lengkap
  dengan susunan subfoldernya, termasuk folder kosong.
- **Penerima yang memutuskan.** Setiap paket menampilkan pengirim, daftar file, ukuran, dan folder simpan,
  lalu menunggu **Terima** atau **Tolak** (otomatis ditolak setelah 2 menit). Opsi **Langsung terima tanpa
  tanya** tersedia untuk PC milik sendiri.
- **Diperiksa sampai utuh.** Setiap file dicek dengan SHA-256 setelah tiba. File yang rusak di jalan ditolak,
  dan file setengah jadi (`.kdpart`) dihapus kalau transfer batal atau putus.
- **Tidak menimpa apa pun.** Nama yang sudah ada diberi nomor: `foto.jpg` → `foto (1).jpg`, `Album` → `Album (1)`.
- **Slip pengiriman**: progres, kecepatan, sisa waktu, tombol **Batal**, dan **Buka** untuk melihat hasilnya
  di Explorer.
- **Tambah lewat IP** untuk jaringan yang memblokir broadcast (misalnya Wi-Fi kantor atau hotel).
- **Nama PC bisa diganti** langsung di label sendiri; PC lain melihat nama baru seketika.

## Cara install

1. Download `kirimdong-<versi>-win-x64.msi` dari [halaman rilis](https://github.com/farishiddiq/kirimdong/releases/latest).
2. Jalankan. Terinstal untuk akun kamu saja (tidak perlu hak administrator), lengkap dengan shortcut Start Menu
   dan Desktop. Runtime .NET sudah termasuk.
3. Lakukan di **kedua** PC.

> Installer belum ditandatangani (code signing), jadi Windows SmartScreen bisa menampilkan
> "Windows protected your PC". Pilih **More info → Run anyway**.

Uninstall lewat **Settings → Apps → Installed apps**. Pengaturan di `%APPDATA%\kirimdong` dan file yang
sudah diterima tidak ikut terhapus.

## Cara pakai

1. Buka **kirimdong** di dua PC yang tersambung ke Wi-Fi (atau router) yang sama.
2. Saat pertama dibuka, Windows menanyakan izin firewall. Pilih **Izinkan** untuk jaringan **Private**.
   Kalau jaringan Wi-Fi kamu tercatat sebagai *Public*, ubah ke *Private* di Settings → Network & internet.
3. Label PC lain muncul di kanan. Seret file/folder ke labelnya, atau tekan **Pilih file**.
4. Di PC penerima, tekan **Terima**. File masuk ke `Downloads\kirimdong` (bisa diganti lewat **Ganti folder**).

PC tidak muncul? Lihat IP di label PC tujuan (misalnya `192.168.1.20`), lalu ketik di kotak
**Tambah lewat IP** pada PC pengirim.

## Privasi dan keamanan

- Semua lewat jaringan lokal kamu. Tidak ada server, akun, iklan, atau telemetri; app ini tidak menghubungi internet.
- Yang dikirim ke jaringan saat app terbuka: nama PC (yang tertulis di label) dan port penerima, supaya PC lain bisa menemukannya.
- Paket dari PC lain tidak disimpan sebelum kamu menekan **Terima** (kecuali kamu menyalakan *Langsung terima tanpa tanya*).
- Nama file dari PC lain dibersihkan sebelum disimpan, sehingga file tidak bisa keluar dari folder simpan (`..\`, path absolut, nama perangkat Windows).
- Pengaturan tersimpan di `%APPDATA%\kirimdong\settings.json`.

## Keterbatasan

- **Belum terenkripsi.** Isi file berjalan di jaringan lokal tanpa enkripsi. Aman untuk Wi-Fi rumah atau kantor
  yang kamu percaya; jangan dipakai untuk file sensitif di Wi-Fi publik.
- Hanya Windows 10/11 (64-bit). Belum ada versi Android/macOS.
- Pencarian otomatis hanya di satu subnet. Router yang memisahkan perangkat (*client isolation*, umum di Wi-Fi
  tamu) memblokir koneksi antar-PC sama sekali.
- Transfer yang putus harus diulang dari awal (belum bisa dilanjutkan).
- Kecepatan mengikuti jaringan: lewat Wi-Fi biasanya jauh lebih lambat daripada kabel LAN.

## Teknologi

- C# / .NET 8, WPF (tanpa library pihak ketiga). Font bawaan Windows: Bahnschrift dan Ink Free.
- `Kirimdong.Core`: discovery UDP (port 47800), transfer TCP (port 47801) dengan protokol sederhana
  (header `KDNG`, penawaran JSON, aliran byte per file + SHA-256), pembersihan path.
- `Kirimdong.App`: UI kardus, label, slip, dan kartu paket masuk. Tekstur kraft digambar sendiri saat start.
- Installer: MSI per-user dengan WiX Toolset 5.

### Build dari source

```powershell
dotnet test tests/Kirimdong.Core.Tests          # 37 test: protokol, path, transfer lewat socket sungguhan
dotnet run --project src/Kirimdong.App
powershell -ExecutionPolicy Bypass -File installer/build.ps1   # → artifacts/kirimdong-<versi>-win-x64.msi
```

Menguji dua salinan di satu PC: `kirimdong.exe --profile a --port 47811` dan `kirimdong.exe --profile b --port 47812`
(pengaturan dan kunci single-instance terpisah per profil).

## Lisensi

[MIT](LICENSE) © 2026 farishiddiq

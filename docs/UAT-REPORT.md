# TriggerCheck: UAT Raporu

**Tarih:** 2026-11-03 · **Commit:** `5a54e42` (main) · **Kapsam:** İş paketi #5

## Özet

- **10 kabul senaryosunun 10'u geçti.** Senaryo 5 notla geçti: aralıklar sistematik olarak +1 sn kayıyor.
- Build'de hata yok; 220 testin 220'si geçiyor.
- **Tenant izolasyonu ve yetkilendirmede açık bulunmadı.** Bu sonuca hem canlı testle hem bağımsız kod incelemesiyle varıldı.
- **Üretime çıkmadan önce düzeltilmesi gerekenler:**
  - SSRF koruması yok (B1).
  - Seed edilen SuperAdmin şifresi her ortamda aynı (B2).
  - Incident yaşam döngüsünde iki hata var (B3, B4).

## Yöntem

- **Ortam:** Uygulama `dotnet run` ile `http://localhost:5300` üzerinde, boş bir SQLite veritabanıyla çalıştırıldı. Monitoring motoru açıktı.
- **Test hedefleri:** Yerel HTTP sunucuları kullanıldı:
  - 8765: sabit UP
  - 8767: açılıp kapatılarak DOWN/UP senaryosu
  - 8780: aralık ölçümü
  - 8770: webhook alıcısı
  - 8799: kapalı port
  - Not: SSRF korumasından (SEC-1) sonra yerel hedefler yalnızca `Monitoring:AllowPrivateTargets=true` ile izlenebilir. Bu bayrak `appsettings.Development.json` içinde açıktır; üretimde varsayılan `false` kalır.
- **Nasıl sürüldü:** Senaryolar gerçek HTTP istekleriyle yürütüldü: login, antiforgery token, form POST. Sonuçlar arayüz yanıtlarından ve veritabanından doğrulandı.
- **Kod incelemesi:** Ayrı bir agent kodu bağımsız olarak, salt okunur şekilde inceledi.
- **Test verisi:** Acme (Standard), Beta (Basic), Ent (Enterprise) müşterileri ve Ent altında bir CustomerViewer kullanıcısı.

## Kabul senaryoları

| # | Senaryo | Sonuç | Kanıt |
|---|---------|-------|-------|
| 1 | SuperAdmin müşteri + plan + admin oluşturur, admin giriş yapar | ✅ | 3 müşteri oluşturuldu, 3 admin girişi başarılı. Yanlış şifre reddedildi. Anonim kullanıcı `/App` ve `/SuperAdmin`'den login'e yönlendirildi. Müşteri `/SuperAdmin`'e gidince AccessDenied aldı. |
| 2 | Plan aralığı ve maks. monitör limiti | ✅ | Standard planda 30 sn ve 15 sn reddedildi ("İzin verilen: 5 dk, 1 dk"), 60 sn kabul edildi. Basic planda ilk 10 monitör oluştu, 11. "monitör limitine ulaştınız" hatasıyla reddedildi (DB'de 10 kayıt). |
| 3 | UP → DOWN (eşik) → incident → bildirim → UP → incident kapanır | ✅ | FailureThreshold=2: ilk hatada incident açılmadı, 2. hatada açıldı. Webhook ile DOWN bildirimi gönderildi. Hedef geri gelince incident `ResolvedAtUtc` ile kapandı ve UP bildirimi gönderildi. |
| 4 | Keyword ve TCP doğru sonuç verir | ✅ | Var olan keyword UP, olmayan keyword DOWN ("Keyword 'NOT-THERE' not found") verdi. Açık TCP portu UP, kapalı port DOWN ("Connection refused") verdi. |
| 5 | 15 / 30 / 60 sn aralıklar gerçekten bu sıklıkta çalışır | ⚠️ Notla geçti | Ölçülen aralıklar 16,0 / 31,0 / 61,0 sn (hem DB'de hem hedef sunucuda). Her döngü sabit +1 sn kayıyor. Bkz. B8. |
| 6 | Müşteri A, müşteri B'nin monitörüne id ile erişemez | ✅ | Başka müşterinin monitörüne ve bildirim kanalına yapılan tüm GET ve POST istekleri 404 döndü (Details, Edit, Delete, Pause, CheckNow, Alerts Edit/Delete). Veride değişiklik olmadı. |
| 7 | CustomerViewer veri değiştiremez | ✅ | Okuma sayfaları 200 döndü. Tüm yazma sayfaları ve handler'ları AccessDenied'a yönlendirdi. Düzenleme butonları arayüzde görünmüyor. CustomerAdmin kendine `SuperAdmin` rolü veremiyor ("Geçersiz rol"). |
| 8 | Askıya alma: giriş yok, açık oturum düşer, kontrol yok | ✅ | Açık oturum bir sonraki istekte `/Account/Login?reason=inactive` sayfasına düştü. Yeni giriş reddedildi. Vadesi geçmiş monitörler 12 sn boyunca kontrol edilmedi. |
| 9 | Plan düşürülünce aralıklar plan minimumuna çekilir | ✅ | Pro → Standard: 30 sn → 60 sn. Standard → Basic: tüm monitörler 300 sn. |
| 10 | Duraklatılmış monitör kontrol edilmez | ✅ | Durum Paused oldu, `IsEnabled=0`. Duraklatıldıktan sonra yeni kontrol sonucu oluşmadı. |

Uygulama logunda beklenmedik bir hata veya exception yok.

## Bulgular

Önem sırasıyla. "Canlı" sütunu, bulgunun UAT sırasında çalışan uygulamada da gözlenip gözlenmediğini gösterir.

| # | Önem | Bulgu | Canlı | Yer |
|---|------|-------|-------|-----|
| B1 | Yüksek | **SSRF.** HTTP, Keyword, TCP ve webhook hedeflerinde iç ağ adresleri kabul ediliyor: `127.0.0.1`, `169.254.169.254`, RFC1918, `::1`. Redirect'ler de takip ediliyor. Durum kodu, hata metni ve keyword eşleşmesi müşteriye geri döndüğü için iç servisler taranabilir, cloud metadata sızdırılabilir. | ✅ UAT bu açığı kullanarak yerel sunucuları izledi | `MonitorInput.cs:77-93`, `AlertChannelInput.cs:44`, `HttpCheckers.cs:53-61` |
| B2 | Yüksek | **Seed şifreleri.** `SuperAdmin123!` ve demo verisi `appsettings.json` içinde. Seeder her ortamda çalışıyor, yani üretime de bu hesaplarla çıkılır. | ✅ | `appsettings.json`, `DbSeeder.cs:38-60` |
| B3 | Orta | **Resume sonrası incident açık kalıyor.** Resume durumu `Unknown` yapıyor. Sonraki başarılı kontrol "ilk UP" sayılıyor; açık incident kapanmıyor, UP bildirimi gitmiyor. SuperAdmin pause, App tarafının aksine incident'ları kapatmıyor. | ✅ Monitör 14 UP oldu, incident #3 açık kaldı, UP bildirimi yok | `MonitorStateMachine.cs:35-37`, `SuperAdmin/Pages/Monitors/Index.cshtml.cs:60-82` |
| B4 | Orta | **Check runner ile UI yarışı.** Runner monitörü track edip probe sonrası kaydediyor. Probe sırasında yapılan pause veya edit sessizce eziliyor; duraklatılmış monitör için DOWN ve yeni incident oluşabiliyor. Concurrency token yok. | Kod | `MonitorCheckRunner.cs:38-67` |
| B5 | Orta | **Webhook URL'leri log'a açık yazılıyor.** Slack, Teams gibi webhook URL'leri gizli anahtar içerir. | ✅ `Alert ... sent via Webhook to http://127.0.0.1:8770/hook` | `AlertDispatcher.cs:76-79` |
| B6 | Orta | **SMTP gönderiminde zaman aşımı yok.** `SendMailAsync`'e iptal edilmeyen bir token veriliyor. Yanıt vermeyen bir SMTP sunucusu kontrol slotlarını bloke edebilir ve scheduler durabilir. | Kod (olası) | `Notifiers.cs:46-60` |
| B7 | Düşük–Orta | **"Şimdi kontrol et" için sınır yok.** Rate limit yok; plan aralığını ve eşzamanlılık limitini aşıyor. B1'i hızlandırıyor. | Kod | `Monitors/Details.cshtml.cs:129-160` |
| B8 | Düşük | **Aralık kayması.** Bir sonraki kontrol, planlanan zamandan değil fiili çalışma zamanından hesaplanıyor. 15 sn'lik monitör saatte 240 yerine 225 kez çalışıyor. | ✅ +1,0 sn/döngü | `MonitorStateMachine.cs:29`, `MonitorCheckRunner.cs:41` |
| B9 | Düşük | **Rol düşürme gecikmeli.** Viewer'a düşürülen admin, security stamp doğrulamasına kadar (≤1 dk) yazma yetkisini koruyor. | Kod | `Program.cs:31`, `ActiveAccountFilter.cs` |
| B10 | Düşük | **Maks. monitör kontrolünde yarış.** Sayım ve ekleme arasında transaction yok. Paralel POST'larla limit 1 aşılabilir. | Kod (olası) | `Monitors/Create.cshtml.cs:33-57` |
| B11 | Düşük | **Kanalları kapatmak bildirimi susturmuyor.** Tüm kanallar kapalıyken bildirimler `ContactEmail` adresine gidiyor. Kasıtlı olabilir, ama arayüzde belirtilmemiş. | Kod | `AlertDispatcher.cs:36-47` |
| B12 | Düşük | **Bakım işleri.** Retention yalnızca `CheckResults` tablosunu temizliyor; Incidents ve Notifications sınırsız büyüyor. `MonitorRunGate` silinen monitörlerin semaphore'larını tutmaya devam ediyor. SQLite WAL modunda değil. Müşteri oluşturma hatasında `ex.Message` gösteriliyor. | Kod | `RetentionCleanup.cs:29-31` vb. |
| B13 | Bilgi | **Askıya alınan kullanıcıya genel hata mesajı.** Askıya alınan müşterinin kullanıcısı "Geçersiz giriş denemesi" görüyor. Güvenlik açısından makul, ama destek talebi yaratabilir. | ✅ | `Login.cshtml.cs:33-36` |

## Sağlam bulunan alanlar

- **Tenant kapsamı:** Bütün App handler'ları `RequiredCustomerId` ile filtreliyor. Ayrı bir AJAX veya grafik endpoint'i yok.
- **Over-posting:** Formlar ayrı input modelleri kullanıyor; `CustomerId` veya `PlanId` bağlanamıyor. Roller beyaz listeyle sınırlı.
- **Antiforgery ve redirect:** Antiforgery varsayılan olarak açık. Logout yalnızca POST ile yapılıyor. `returnUrl`, `IsLocalUrl` ile kontrol ediliyor.
- **XSS:** `Html.Raw` hiç kullanılmıyor.
- **Motor sağlamlığı:**
  - Her kontrol kendi DbContext'iyle çalışıyor.
  - Aynı monitör için üst üste binen çalıştırmalar engelleniyor.
  - Hatalar scheduler döngüsünü durdurmuyor.
  - Probe'larda timeout uygulanıyor.
  - Aynı monitör için mükerrer incident açılması engelleniyor.

## Önerilen sıra

1. **B2, B3, B5, B8:** Küçük ve yerel düzeltmeler.
2. **B1 ve B7:** `SocketsHttpHandler.ConnectCallback` ile IP doğrulaması (redirect ve DNS rebinding dahil), aynısı TCP ve webhook için. "Şimdi kontrol et" için cooldown.
3. **B4 ve B6:** Monitör durumuna concurrency token. Bildirim gönderimini kontrol slotundan ayırmak (kuyruk) ve gönderime timeout eklemek.
4. **B9–B12:** Yol haritasına.

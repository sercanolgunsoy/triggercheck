# TriggerCheck: Çalışma Planı

Pingdom / UptimeRobot benzeri, çok kiracılı (multi-tenant) uptime izleme platformu.

## Hedefler
1. **Müşteri paneli** (`/App`): müşteri kendi monitörlerini, bildirim kanallarını, kullanıcılarını yönetir; uptime / yanıt süresi / olay geçmişini görür.
2. **SuperAdmin paneli** (`/SuperAdmin`): platform operatörü müşterileri, planları, müşteri kullanıcılarını yönetir; tüm sistemi izler.
3. **İzleme motoru**: web siteleri ve servisler plan aralığında (5 dk / 1 dk / 30 sn / 15 sn) kontrol edilir; kesinti tespit edilir, olay (incident) açılır/kapanır, bildirim gönderilir.
4. **Kalite**: unit + integration testleri, ardından bağımsız UAT.

## Teknoloji
- .NET 9, ASP.NET Core Razor Pages, ASP.NET Core Identity (cookie auth, roller)
- EF Core + SQLite (geliştirme/UAT). Üretimde PostgreSQL/SQL Server'a geçiş için yalnızca `AddInfrastructure` değişir.
- İzleme motoru: `BackgroundService` + `IHttpClientFactory` + `TimeProvider`, aynı process'te barınır (`Monitoring:Enabled`).
- xUnit + `WebApplicationFactory` (integration testler).

## Planlar (paketler)
| Plan       | Min. aralık | Maks. monitör | Saklama |
|------------|-------------|---------------|---------|
| Basic      | 5 dk (300s) | 10            | 30 gün  |
| Standard   | 1 dk (60s)  | 50            | 90 gün  |
| Pro        | 30 sn       | 200           | 180 gün |
| Enterprise | 15 sn       | 1000          | 365 gün |

Kurallar `TriggerCheck.Domain/PlanRules.cs` içindedir (tek doğruluk kaynağı).

## Roller
- `SuperAdmin`: müşteriye bağlı değil. Müşteri/plan/kullanıcı yönetimi.
- `CustomerAdmin`: kendi müşterisinde tam yetki.
- `CustomerViewer`: kendi müşterisinde salt okunur.

## Monitör tipleri
HTTP(S) durum kodu, Keyword (gövdede metin arama), TCP port, Ping (ICMP).
`FailureThreshold` art arda hatalı kontrol sayısına ulaşınca DOWN olur (flapping önleme); ilk başarılı kontrolde UP olur.

## Solution yapısı
```
src/TriggerCheck.Domain          Entity'ler, enum'lar, Roles, PlanRules
src/TriggerCheck.Infrastructure  AppDbContext, ApplicationUser, DbSeeder, DI
src/TriggerCheck.Monitoring      Checker'lar, scheduler, alerting, UptimeStatsService
src/TriggerCheck.Web             Razor Pages; Areas/App (müşteri), Areas/SuperAdmin
tests/TriggerCheck.Tests         Unit + integration (TestWebAppFactory)
```

## İş paketleri
| # | İş paketi | Sahip | Sahip olunan dosyalar |
|---|-----------|-------|-----------------------|
| 0 | Temel: solution, domain, DB, Identity, roller, layout, test altyapısı | Ana agent | Domain, Infrastructure, Web/Program.cs, Web/Pages, Web/Infrastructure, tests/Infrastructure |
| 1 | İzleme motoru: checker'lar, scheduler, durum makinesi, incident, bildirim (e-posta/webhook), retention | Subagent A | src/TriggerCheck.Monitoring/**, tests/TriggerCheck.Tests/Monitoring/** |
| 2 | Müşteri paneli: dashboard, monitör CRUD + plan kuralları, detay (uptime, grafik), olaylar, bildirim kanalları, kullanıcılar, hesap/plan | Subagent B | src/TriggerCheck.Web/Areas/App/**, tests/TriggerCheck.Tests/App/** |
| 3 | SuperAdmin paneli: genel bakış, müşteri CRUD + askıya alma, plan CRUD, plan değişiminde aralık düşürme, müşteri kullanıcıları, tüm monitörler | Subagent C | src/TriggerCheck.Web/Areas/SuperAdmin/**, tests/TriggerCheck.Tests/SuperAdmin/** |
| 4 | Entegrasyon: birleştirme, build, tüm testler | Ana agent | n/a |
| 5 | UAT: uygulamayı gerçek çalıştırıp senaryoları uçtan uca doğrulama + bağımsız kod incelemesi | Subagent D (+ ana agent) | docs/UAT-REPORT.md |

1, 2 ve 3 paralel yürür. Paylaşılan dosyalar (Domain, Infrastructure, Program.cs, _Layout) yalnızca ana agent tarafından değiştirilir; değişiklik ihtiyacı raporlanır.

## Kabul kriterleri (UAT senaryoları)
1. SuperAdmin müşteri oluşturur, plan atar, müşteri admin kullanıcısı tanımlar; kullanıcı giriş yapabilir.
2. Müşteri, planının izin vermediği aralığı (örn. Standard'da 30 sn) seçemez; maks. monitör sayısını aşamaz.
3. Erişilebilir bir HTTP hedefi UP olur; erişilemeyen hedef `FailureThreshold` sonrası DOWN olur, incident açılır, bildirim kaydı oluşur; hedef geri gelince incident kapanır.
4. Keyword, TCP monitörleri doğru sonuç verir.
5. 15 sn / 30 sn / 60 sn aralıklar gerçekte bu sıklıkta çalışır (±tolerans).
6. Müşteri A, müşteri B'nin monitörünü id ile açamaz/düzenleyemez/silemez (tenant izolasyonu).
7. CustomerViewer veri değiştiremez.
8. SuperAdmin müşteriyi askıya alınca: kullanıcıları giriş yapamaz, açık oturumlar düşer, monitörleri kontrol edilmez.
9. Müşteri daha yavaş plana düşürülünce hızlı monitörlerin aralığı plan minimumuna çekilir.
10. Paused monitör kontrol edilmez.

## Bilinen kısıtlar / yol haritası
- Şema `EnsureCreated` ile oluşturuluyor; üretime çıkmadan EF migrations'a geçilmeli.
- Tek scheduler örneği varsayılıyor; yatay ölçekte DB tabanlı kilit/lease veya kuyruk (örn. RabbitMQ) gerekir.
- Çok bölgeli (multi-region) kontrol, SMS/Slack entegrasyonu, public status page, SSL sertifika süresi kontrolü: sonraki faz.

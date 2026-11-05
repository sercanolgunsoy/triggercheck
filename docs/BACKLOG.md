# TriggerCheck: Backlog

**Güncelleme:** 2026-11-04 · **Dal:** `redesign` · **Kaynaklar:** [PLAN.md](../PLAN.md), [UAT-REPORT.md](UAT-REPORT.md)

Bu dosya ürünün yapılacak işlerini tek yerde toplar. UAT bulguları (B1–B13) kendi ID'leriyle referans verilir; detayları UAT raporundadır.

## Okuma kılavuzu

| Alan | Değerler |
|------|----------|
| **Öncelik** | **P0** üretim engelleyici · **P1** ilk sürümde olmalı · **P2** sonraki sürüm · **P3** fikir / araştırma |
| **Boyut** | **S** ≤ 1 gün · **M** 2–4 gün · **L** 1–2 hafta · **XL** > 2 hafta (bölünmeli) |
| **Durum** | `Açık` · `Devam` · `Bekliyor` (karar/bağımlılık) · `Bitti` |

ID formatı: `<EPIC>-<no>`. Bağımlılıklar "→" ile gösterilir.

## Epic özeti

| Epic | Amaç | Öncelik |
|------|------|---------|
| **SEC** | Üretim güvenliği (UAT yüksek/orta bulguları) | P0 |
| **ENG** | İzleme motoru doğruluğu ve dayanıklılığı | P0–P1 |
| **OPS** | Üretime çıkış altyapısı (migrations, DB, ölçek, gözlemlenebilirlik) | P0–P2 |
| **UI** | `redesign` dalının tamamlanması, pazarlama sayfaları | P1 |
| **FEAT** | Ürün özellikleri (SSL, status page, entegrasyonlar…) | P1–P3 |
| **BIZ** | Kayıt, ödeme, plan yönetimi | P2 |
| **AI** | Yapay zekâ ile yorumlama ve karar mekanizmaları | P1–P3 |

---

## SEC: Üretim güvenliği

| ID | İş | Öncelik | Boyut | Kabul kriteri | Ref |
|----|----|---------|-------|---------------|-----|
| SEC-1 | **SSRF koruması.** `SocketsHttpHandler.ConnectCallback` ile bağlanılan IP'yi doğrula (loopback, link-local, RFC1918, ULA, `169.254.169.254` reddedilir). Redirect ve DNS rebinding dahil. Aynı kontrol TCP, Ping ve webhook için. | P0 | M | İç adrese işaret eden monitör/webhook kaydedilemez; kayıtlıysa probe "Blocked target" ile düşer. Redirect ile iç ağa gitme testi kırmızı → yeşil. Geliştirme için `Monitoring:AllowPrivateTargets` bayrağı (varsayılan `false`). | B1 |
| SEC-2 | **Seed şifreleri.** Üretimde seeder demo veriyi oluşturmaz; SuperAdmin şifresi ortam değişkeni/secret'tan gelir, yoksa uygulama başlamaz. İlk girişte şifre değişimi zorunlu. | P0 | S | `appsettings.json` içinde şifre yok. `Production` ortamında boş şifreyle başlatma hata verir. | B2 |
| SEC-3 | **Webhook URL'lerini maskele.** Log ve UI'da yalnızca host + `…` gösterilir. | P0 | S | Log çıktısında path/query görünmez. | B5 |
| SEC-4 | **"Şimdi kontrol et" rate limit.** Monitör başına cooldown (plan min. aralığı veya 30 sn, hangisi büyükse) + tenant başına dakikalık kota. | P1 | S | Cooldown içinde ikinci istek 429 / kullanıcı mesajı döner. | B7 |
| SEC-5 | **Rol değişiminde anında yetki düşürme.** Rol değişince `UpdateSecurityStampAsync`; doğrulama aralığı kısaltılır. | P2 | S | Viewer'a düşürülen kullanıcının bir sonraki yazma isteği reddedilir. | B9 |
| SEC-6 | **Hata mesajı sızıntısı.** Müşteri oluşturma hatasında `ex.Message` yerine genel mesaj + log korelasyon ID'si. | P2 | S | UI'da exception metni görünmez. | B12 |
| SEC-7 | **Güvenlik başlıkları.** CSP, HSTS, `X-Content-Type-Options`, `Referrer-Policy`. Login'e brute-force kilidi (Identity lockout) açık mı kontrol. | P1 | S | securityheaders.com benzeri kontrol A. | n/a |

## ENG: İzleme motoru

| ID | İş | Öncelik | Boyut | Kabul kriteri | Ref |
|----|----|---------|-------|---------------|-----|
| ENG-1 | **Resume sonrası açık incident.** Resume durumu `Unknown` yaparken açık incident varsa `Down` olarak devam etsin veya resume'da incident kapatılsın; SuperAdmin pause da App ile aynı yolu kullansın (tek servis). | P0 | S | UAT senaryosu: Down → pause → resume → Up ⇒ incident kapanır, UP bildirimi gider. | B3 |
| ENG-2 | **Runner / UI yarışı.** `Monitor`'a concurrency token (`RowVersion`). Runner probe sonrası yalnızca runtime alanlarını günceller; çakışmada yeniden okuyup pause/edit'e saygı gösterir. | P0 | M | Probe sırasında pause edilen monitör için incident açılmaz (entegrasyon testi). | B4 |
| ENG-3 | **Aralık kayması.** `NextCheckAtUtc = önceki planlanan zaman + aralık` (geride kaldıysa şimdiye hizala). | P1 | S | 15 sn monitör saatte 240 ± 2 kez çalışır. | B8 |
| ENG-4 | **Bildirimi kontrol slotundan ayır.** `Channel<AlertMessage>` tabanlı kuyruk + ayrı gönderici; SMTP ve webhook için timeout ve retry (üstel bekleme). | P1 | M | Yanıt vermeyen SMTP sunucusu kontrolleri durdurmaz. Başarısız gönderim `Notification.Success=false` + yeniden deneme sayısı. | B6 |
| ENG-5 | **Maks. monitör yarışı.** Sayım + ekleme tek transaction / serializable veya DB kısıtı. | P2 | S | 20 paralel POST'ta limit aşılmaz. | B10 |
| ENG-6 | **Kanal kapatma davranışı.** "Tüm kanallar kapalı ⇒ ContactEmail" kuralı UI'da açıkça yazılsın; kullanıcı "bildirim gönderme" seçebilsin. | P2 | S | Ayar sayfasında açıklama + seçenek. | B11 |
| ENG-7 | **Bakım işleri.** Retention'a Incidents/Notifications ekle; `MonitorRunGate`'te silinen monitörlerin semaphore'larını temizle; SQLite WAL modu. | P2 | S | 1 yıllık veri simülasyonunda tablolar plan saklama süresine göre küçülür. | B12 |
| ENG-8 | **Yanıt süresi metrikleri.** CheckResult'a DNS / connect / TLS / TTFB ayrımı (HTTP için). AI-2 ve AI-3'ün girdisi. | P1 | M | Detay sayfasında aşama kırılımı görünür. | n/a |
| ENG-9 | **Doğrulama probe'u (confirm before alert).** DOWN'a geçmeden önce aynı hedefe kısa aralıkla bir doğrulama denemesi; ileride ikinci bölgeden (FEAT-6). AI-4 için temel. | P1 | M | Tek seferlik ağ hıçkırığı incident açmaz; gerçek kesinti tespit süresi ≤ aralık + 10 sn. | n/a |

## OPS: Üretime çıkış

| ID | İş | Öncelik | Boyut | Kabul kriteri | Ref |
|----|----|---------|-------|---------------|-----|
| OPS-1 | **EF Core migrations.** `EnsureCreated` kaldırılır, ilk migration oluşturulur; başlangıçta migrate (veya ayrı adım). Paralel geliştirmede snapshot çakışmalarını önlemek için bilinçli olarak en sona bırakıldı. **O zamana kadar** her dalga yeni kolon/tablo eklediği için (ör. `Monitor.Version`, `Monitor.Cert*`, `Monitor.PendingConfirmation*`, `CheckResult.DnsMs/ConnectMs/TlsMs/TtfbMs/IsConfirmation`, `AlertChannel.Secret`, `Notification.Attempts`, `Customer.AlertContactEmailWhenNoChannel` (migration'da DB varsayılanı **true** olmalı), `Plan.AllowsSmsVoice`, `SmsVoiceUsage`, `ApiKeys`, `AspNetUsers.MustChangePassword`, `MaintenanceWindows` + `MaintenanceWindowMonitors`, `CheckResult.InMaintenance`, `Incident.ResolvedDuringMaintenance/AcknowledgedAtUtc/AcknowledgedByUserId/AcknowledgeNote/LastEscalationStep/LastEscalationDelayMinutes`, `EscalationPolicies` (`EnabledAtUtc`) + `EscalationSteps` + `EscalationStepChannels`, FEAT-9: `Monitor.DnsRecordType/JsonPath/AssertionOperator/ExpectedValue/GracePeriodSeconds/HeartbeatTokenHash/HeartbeatTokenPrefix/LastHeartbeatAtUtc/LastHeartbeatFailed/LastHeartbeatMessage` + unique index on `Monitor.HeartbeatTokenHash`, FEAT-4: `StatusPages` (unique `Slug`, non-unique index on `CustomDomain`, `CustomDomainCheckedAtUtc/CustomDomainFailures`) + `StatusPageMonitors` + `StatusPageSubscribers` + `StatusPageNotes` + `StatusPageEvents` (unique `StatusPageId+Key`, `MonitorId`, `Mailed`), `Plan.MaxStatusPages` (nullable = sınırsız; migration'da mevcut planlar için seed değerleri Basic 1 / Standard 3 / Pro 10 / Enterprise NULL) + `Plan.AllowsStatusPageCustomDomain`, FEAT-10: `Monitor.SlaTarget`, `Customer.MonthlyReportEmail/ReportTimeZoneId`, `MonthlyReportDeliveries` (unique `CustomerId+Month`), FEAT-6: `Regions` (unique `Code`, unique `Prefix`) + `MonitorRegionStates` (PK `MonitorId,RegionId`; indexes `RegionId,NextCheckAtUtc`, `LeaseItemId`, `RegionId,LastResultItemId`), `CheckResult.RegionId/VotedUp`, `Incident.FailedRegions`, `Plan.MaxRegions` (migration'da DB varsayılanı **1** olmalı), `Monitor.LastProbeFingerprint/RegionsExpeditedAtUtc`, OPS-3: `Monitor.LeasedUntilUtc/LeaseOwner` + `ClusterLeases` (PK `Name`; `Owner`, `LeasedUntilUtc`), AI-5: `SettingsSuggestions` (`MonitorId` FK cascade; `Kind`, `Status`, `CurrentValue`/`SuggestedValue` max 600, `EvidenceJson`, `CreatedAtUtc`, `EvidenceUpdatedAtUtc`, `ClosedAtUtc`, `ClosedByUserId` max 450; index `MonitorId+Kind+Status`, `CustomerId+Status`) + `Monitor.SettingsChangedAtUtc`), AI-7: `AiDecision.ModelCalls` (int, migration'da DB varsayılanı **0**) mevcut yerel `triggercheck.db` silinmelidir. | P0 | S | Boş DB ve `EnsureCreated` ile oluşturulmuş mevcut DB üzerinde migrate sorunsuz (mevcut DB baseline'lanır). | PLAN |
| OPS-2 | **PostgreSQL desteği.** Provider seçimi config ile; testler iki provider'da da koşar. | P1 | M | CI'da Postgres container'ı ile testler yeşil. | PLAN |
| OPS-3 | **Çoklu instance scheduler.** DB tabanlı lease (`NextCheckAtUtc` + `LeasedUntil` + `SELECT … FOR UPDATE SKIP LOCKED`) veya kuyruk. **Durum (uygulandı):** `Monitor.LeasedUntilUtc/LeaseOwner` üzerinde taşınabilir koşullu `ExecuteUpdate` (compare-and-set; SKIP LOCKED yalnızca ileride Postgres optimizasyonu); planlı kontrol ve "şimdi kontrol et" aynı lease'i tutar, çöken instance'ın lease'i süre dolunca (sabit `Monitoring:InstanceId` ile açılışta hemen) geri alınır. Küme genelinde tek çalışması gereken işler (faturalama bakımı, AI olay izleyici, AI baseline) `ClusterLeases` ile lider seçimi + yan etkilerden önce yenileme (fencing); bölge lease'ine de aynı "hâlâ vadesi gelmiş" koşulu eklendi. Diğer arka plan işleri denetlendi (DEPLOYMENT.md "Running multiple instances"). | P2 | L | 2 instance'ta aynı monitör iki kez kontrol edilmez. | PLAN |
| OPS-4 | **Gözlemlenebilirlik.** OpenTelemetry (trace + metric): kontrol sayısı, gecikme, kuyruk derinliği, bildirim hataları. Health endpoint (`/healthz`, scheduler heartbeat dahil). | P1 | M | Dashboard'da "scheduler son tur" metriği; 2 dk tur yoksa health kırmızı. | n/a |
| OPS-5 | **CI/CD.** Build + test + format kontrolü; container image; ortam başına config. | P1 | M | `main`'e merge'de image üretilir. | n/a |
| OPS-6 | **Yedekleme ve geri yükleme prosedürü.** | P2 | S | Dokümante edilmiş ve bir kez denenmiş. | n/a |

## UI: Redesign ve arayüz

| ID | İş | Öncelik | Boyut | Kabul kriteri | Ref |
|----|----|---------|-------|---------------|-----|
| UI-1 | **`redesign` dalını tamamla.** Pazarlama layout'u, Features, Pricing, Auth layout, tema scripti, toolbar, sidebar, ikonlar. Commit edilmemiş değişiklikler gözden geçirilip birleştirilir. | P1 | M | Tüm sayfalar açık/koyu temada ve 375 px genişlikte bozulmadan çalışır; testler yeşil. | git status |
| UI-2 | **Lokalizasyon kapsamı.** Yeni sayfalardaki metinlerin `SharedResource.tr.resx`'e taşınması; eksik anahtar testi. | P1 | S | TR modunda İngilizce metin kalmaz (otomatik test). | n/a |
| UI-3 | **Erişilebilirlik.** Kontrast, klavye ile gezinme, durum rozetlerinde yalnızca renge dayanmama. | P2 | S | Lighthouse a11y ≥ 95. | n/a |
| UI-4 | **Monitör detay grafikleri.** Yanıt süresi zaman serisi + aşama kırılımı (ENG-8) + incident işaretleri. | P1 | M | 24 sa / 7 g / 30 g aralıkları. | n/a |
| UI-5 | **Askıya alınmış kullanıcı mesajı.** Genel hata yerine "hesabınız askıda, yöneticinizle iletişime geçin" (güvenlik değerlendirmesiyle). | P3 | S | n/a | B13 |

## FEAT: Ürün özellikleri

| ID | İş | Öncelik | Boyut | Kabul kriteri | Ref |
|----|----|---------|-------|---------------|-----|
| FEAT-1 | **SSL sertifika süresi kontrolü.** Kalan gün eşiği (30/14/7) ile uyarı; zincir ve hostname hatası. | P1 | M | Süresi 10 gün kalan sertifika için uyarı oluşur. | PLAN |
| FEAT-2 | **Slack / Microsoft Teams / Telegram kanalları.** Biçimlendirilmiş mesaj. | P1 | M | Her kanal için test bildirimi butonu. | PLAN |
| FEAT-3 | **Bakım pencereleri.** Planlı bakımda kontrol devam eder, incident/bildirim bastırılır, uptime hesabından çıkarılır. | P1 | M | Bakım sırasında DOWN bildirim üretmez. | n/a |
| FEAT-4 | **Public status page.** Müşteri başına seçili monitörler, incident geçmişi, abonelik. | P2 | L | Özel alan adı (CNAME) desteği. | PLAN |
| FEAT-5 | **Eskalasyon politikaları.** X dk içinde onaylanmazsa ikinci kanala; incident "onaylandı" (ack) durumu. | P2 | M | Ack edilen incident tekrar bildirim üretmez. | n/a |
| FEAT-6 | **Çok bölgeli kontrol.** Hafif probe ajanları; çoğunluk oylaması ile DOWN kararı. | P3 | XL | 3 bölgeden 2'si hata derse DOWN. | PLAN |
| FEAT-7 | **SMS / sesli arama.** Enterprise planına özel. | P3 | M | n/a | PLAN |
| FEAT-8 | **REST API + API anahtarı.** Monitör CRUD, durum, incident listesi (tenant kapsamlı). | P2 | L | OpenAPI dokümanı; anahtar başına rate limit. | n/a |
| FEAT-9 | **Yeni monitör tipleri.** DNS kaydı, heartbeat/cron (push tabanlı), JSON yanıt doğrulama. | P2 | M/tip | n/a | n/a |
| FEAT-10 | **Raporlar.** Aylık SLA raporu (PDF/e-posta). AI-6 ile birleşebilir. | P2 | M | n/a | n/a |

## BIZ: İş modeli

| ID | İş | Öncelik | Boyut | Kabul kriteri |
|----|----|---------|-------|---------------|
| BIZ-1 | **Self-servis kayıt ve deneme süresi** (Pricing'deki "Request access" yerine). | P2 | L | E-posta doğrulama, 14 gün deneme, otomatik Basic'e düşüş. |
| BIZ-2 | **Ödeme entegrasyonu** (iyzico / Stripe). | P2 | L | Plan yükseltme/düşürme self-servis; fatura geçmişi. |
| BIZ-3 | **Plan kullanım göstergeleri.** Monitör kotası, AI kotası (AI-10) panelde görünür. | P2 | S | Kotanın %80'inde uyarı. |

---

## AI: Yorumlama ve karar mekanizmaları

### Temel ilke

**Durumu kurallar belirler, yapay zekâ yorumlar ve önerir.** UP/DOWN kararı, incident açma/kapama ve bildirim gönderimi deterministik motorda kalır (`MonitorStateMachine`). Bu kararlar test edilebilir, açıklanabilir ve müşteriye karşı savunulabilir olmalı. AI bu çekirdeğin **etrafında** çalışır: veriyi yorumlar, gürültüyü azaltmak için öneri üretir, insan onayıyla ya da düşük riskli alanlarda sınırlı özerklikle aksiyon alır.

AI çağrısı başarısız olursa, zaman aşımına uğrarsa veya kapatılırsa ürün AI'sız haliyle eksiksiz çalışmaya devam eder.

### Karar katmanları

```
 Probe sonucu
     │
 L0  Deterministik kurallar      MonitorStateMachine, FailureThreshold, plan kuralları
     │                            → UP/DOWN, incident aç/kapa, bildirim. Her zaman çalışır.
 L1  İstatistiksel sinyaller     Baseline, anomali skoru, korelasyon (LLM yok, ucuz, her probe'da)
     │                            → "yavaşlama", "aynı host'ta 5 monitör düştü" gibi sinyaller
 L2  LLM yorumlama               Yalnızca L0/L1 tetiklediğinde (incident, anomali, rapor, kullanıcı sorusu)
     │                            → özet, olası kök neden, öneri; yapılandırılmış çıktı + güven skoru
 L3  Aksiyon politikası          Özerklik seviyesine göre: göster / öner / onayla-uygula / otomatik
                                  → her karar AiDecision tablosuna yazılır
```

L1'in LLM'den ayrı tutulmasının nedeni maliyet ve gecikme: saniyede yüzlerce probe için LLM çağrısı hem pahalı hem gereksiz. Sayısal anomali tespiti istatistikle daha iyi yapılır; LLM'in değeri bu sinyalleri insanın anlayacağı dile çevirmekte ve bağlamı birleştirmektedir.

### Özerklik seviyeleri

| Seviye | Ad | Ne olur | Örnek |
|--------|----|---------|-------|
| A0 | Bilgi | Yalnızca gösterilir; hiçbir şey değişmez. | Incident özeti |
| A1 | Öneri | Kullanıcıya önerilir, tek tıkla uygulanır. | "FailureThreshold'u 3 yapın" |
| A2 | Onaylı aksiyon | AI aksiyonu hazırlar, CustomerAdmin onaylar. | Status page incident metni yayınlama |
| A3 | Sınırlı otomatik | Kural sınırları içinde otomatik; geri alınabilir, loglanır, müşteri kapatabilir. | İlişkili incident'ları tek bildirimde gruplama |

**Kural:** L0 kararlarını (UP/DOWN, incident aç/kapa) hiçbir AI özelliği A3 ile değiştiremez. Bir özelliğin seviyesi ancak ölçülmüş doğrulukla (AI-11) yükseltilir.

### Karar matrisi

| Karar | Kim karar verir | AI'ın rolü | AI yoksa / hata verirse |
|-------|-----------------|------------|-------------------------|
| Monitör UP/DOWN | L0 kuralları | Yok | n/a |
| Incident aç/kapa | L0 kuralları | Yok | n/a |
| DOWN bildirimi gönder | L0 + ENG-9 doğrulama probe'u | A3: ilişkili incident'ları gruplar (AI-4) | Her incident ayrı bildirim |
| Yavaşlama uyarısı | L1 anomali skoru > eşik | A0: nedenini açıklar | Ham uyarı: "p95 3× baseline" |
| Incident'ın olası nedeni | Kullanıcı | A0: hipotez + kanıt + güven | Ham hata mesajı gösterilir |
| Monitör ayarı değişikliği | CustomerAdmin | A1: öneri | Öneri yok |
| Status page / müşteri iletişimi | CustomerAdmin | A2: taslak | Boş form |
| Plan yükseltme önerisi | SuperAdmin / müşteri | A1: sinyal | Öneri yok |

### Güven skoru ve açıklanabilirlik

Her LLM çıktısı yapılandırılmış (JSON şema) döner ve şunları içerir:

- `summary`: 1–3 cümle, kullanıcının dilinde (TR/EN, mevcut lokalizasyondan).
- `hypotheses[]`: her biri `cause`, `confidence` (`low`/`medium`/`high`), `evidence[]` (hangi CheckResult, hangi metrik).
- `suggested_actions[]`: her biri `action`, `autonomy` (A0–A3), `reversible` (bool).
- `unknowns[]`: veride olmayan, kullanıcının kontrol etmesi gerekenler.

Kanıtı olmayan hipotez gösterilmez. `low` güvenli hipotezler UI'da "olası" etiketiyle ve katlanmış olarak gösterilir. Arayüzde her AI içeriği "AI tarafından üretildi" rozeti taşır ve 👍/👎 geri bildirimi alır.

### Veri ve gizlilik

- **Tenant izolasyonu:** LLM bağlamına yalnızca o müşterinin verisi girer; bağlamı oluşturan sorgular `RequiredCustomerId` ile aynı filtreden geçer. Q&A özelliğinde (AI-7) araçlar tenant ID'yi kullanıcıdan değil oturumdan alır.
- **Maskeleme:** URL'ler yalnızca şema + host olarak gönderilir (yol, query string ve kimlik bilgisi atılır); webhook URL'leri, `Authorization`/token/parola benzeri değerler (JSON içindekiler dahil), JWT / API anahtarı biçimindeki dizgiler, e-posta adresleri ve telefon numaraları LLM'e gönderilmeden önce maskelenir (SEC-3 yardımcısı + `AiMasker`). K5 ile uyumlu: yalnızca host, durum kodu, hata türü ve süreler dışarı çıkar.
- **Prompt injection:** Probe yanıt gövdeleri ve hata metinleri dış kaynaklıdır. Bunlar talimat değil veri olarak işaretlenir, kısaltılır ve hiçbir AI çıktısı doğrudan aksiyon tetiklemez (aksiyonlar yalnızca şemadaki sabit listeden seçilir ve L3 politikasından geçer).
- **Opt-in:** AI özellikleri müşteri bazında kapatılabilir; SuperAdmin global kapatma anahtarı (`Ai:Enabled`).

### Model seçimi (karar bekliyor)

Uygulama .NET olduğundan resmi Anthropic C# SDK'sı kullanılır. Önerilen başlangıç:

| Kullanım | Önerilen model | Not |
|----------|---------------|-----|
| Varsayılan (tüm AI özellikleri) | Claude Opus 5.5 (`claude-opus-5-5`) | Effort seviyesi özelliğe göre ayarlanır: özet/sınıflandırma `low`, postmortem ve Q&A `medium`/`high`. |
| Yüksek hacimli işler için aday | Claude Haiku 5.5 (`claude-haiku-5-5`) | Incident özeti ve gruplama gibi kısa, sık işler için **maliyet kararı**: AI-11 eval setinde kalite tutuyorsa geçilir. |

Teknik notlar:
- Çıktılar için structured outputs (`output_config.format`) kullanılır; serbest metin parse edilmez.
- Sistem prompt'u ve şema sabit tutulup prompt caching ile önbelleğe alınır; değişken veri (probe sonuçları) sona konur.
- Rapor gibi gecikmeye duyarsız işler (AI-6) Message Batches ile gece toplu çalıştırılır.
- Model ID'si config'te tutulur; kod sabitlemez.

### AI backlog'u

| ID | İş | Seviye | Öncelik | Boyut | Kabul kriteri | Bağımlılık |
|----|----|--------|---------|-------|---------------|------------|
| AI-0 | **AI altyapısı.** `IAiInterpreter` arayüzü (Monitoring katmanından bağımsız), C# SDK istemcisi, config (`Ai:Enabled`, `Ai:Model`, timeout), maskeleme yardımcısı, `AiDecision` tablosu (girdi hash'i, model, çıktı, güven, seviye, kullanıcı geri bildirimi, maliyet token'ları), kapalıyken no-op uygulama. | n/a | P1 | M | AI kapalıyken tüm testler değişmeden yeşil. Her çağrı `AiDecision`'a yazılır. Çağrı ana akışı bloklamaz (arka plan kuyruğu). | SEC-3 |
| AI-1 | **L1 istatistiksel baseline.** Monitör başına yanıt süresi için kayan medyan + MAD (veya EWMA), saat-of-gün mevsimselliği; anomali skoru. LLM yok. | n/a | P1 | M | Sentetik veride 3× yavaşlama 3 probe içinde işaretlenir; normal gece/gündüz farkı yanlış alarm üretmez (yanlış pozitif < %2). Not: değerlendirme periyodik iş olarak çalışır; tespit gecikmesi = 3 probe aralığı + en fazla `Ai:Baseline:IntervalSeconds` (varsayılan 60 sn). | ENG-8 |
| AI-2 | **Incident yorumlama.** Incident açıldığında ve kapandığında: son N probe, hata türleri, durum kodları, aşama kırılımı, aynı host'taki diğer monitörler, son ayar değişiklikleri → özet + kök neden hipotezleri + öneriler. Incident sayfasında ve DOWN/UP bildiriminde kısa özet. **Durum (uygulandı):** `IncidentInterpretationProvider` + `AiEventWatcher` (Monitoring'e dokunmadan, açılış/kapanışta iş kuyruğa alınır; bildirim beklemez, özet sonradan yalnızca UI'da görünür). Incident detay + liste, dil müşteri ayarından (en/tr). 23 etiketli senaryo `evals/cases/incident-interpretation`; gerçek model skoru AI-11 koşusuyla ölçülür. | A0 | P1 | M | 20 etiketli senaryoda (DNS hatası, TLS süresi dolmuş, 5xx, timeout, keyword kayboldu…) doğru ana neden ≥ %80. Bildirim AI yanıtını beklemez; özet hazırsa sonradan eklenir. | AI-0, ENG-8 |
| AI-3 | **Yavaşlama açıklaması.** AI-1 anomali işaretlediğinde hangi aşamanın (DNS/TLS/TTFB) bozulduğunu ve başlangıç zamanını anlatır; "henüz kesinti yok" uyarısı. **Durum (uygulandı):** `SlowdownExplanationProvider` aşama medyanlarını (önce/sonra, oran) kendisi hesaplar; monitör detayında ham L1 metni + AI açıklaması, panoda "Yavaş" rozeti. 5 etiketli senaryo. | A0 | P2 | S | Uyarı metni hangi aşamayı ve ne kadar saptığını içerir. | AI-1, AI-2 |
| AI-4 | **Alarm gürültüsü azaltma / korelasyon.** Aynı müşteride kısa sürede düşen monitörleri ortak sinyallere göre (aynı host/IP, aynı hata türü, aynı zaman penceresi) grupla; tek "ana incident + etkilenenler" bildirimi. Gruplama kararı önce L1 kurallarıyla, LLM yalnızca belirsiz durumlarda ve açıklama için. | A3 (gruplama) | P2 | L | Gruplama hiçbir incident'ı kaybetmez/kapatmaz; yalnızca bildirimi birleştirir. Müşteri kapatabilir. Grup dışı bırakılan monitör ayrı bildirim alır. | AI-0, ENG-4, ENG-9 |
| AI-5 | **Ayar önerileri.** Geçmiş veriden: flapping varsa FailureThreshold artırma, timeout'a çok yakın yanıtlar için timeout artırma, sürekli DOWN ama hiç UP olmamış monitör için "hedef yanlış olabilir", keyword monitörüne uygun keyword önerisi. **Durum (uygulandı):** kural tabanlı tespit (`SettingsSuggestionRules`, ayar değişikliğinden sonraki geçmiş), DB kanıtı, LLM yalnızca açıklama (sayı doğrulamalı), Uygula normal düzenleme doğrulamasından geçer, Yoksay 30 gün (`Ai:Suggestions:DismissDays`), küme kilidi altında periyodik iş. Gövde saklanmadığı için yeni keyword önerilmez (K5). | A1 | P2 | M | Öneri kanıtıyla birlikte gösterilir ("son 7 günde 14 tekil hata, hepsi 1 probe sonra düzeldi"). Uygula/yoksay; yoksayılan öneri 30 gün tekrar gösterilmez. | AI-0, AI-1 |
| AI-6 | **Haftalık / aylık anlatı raporu.** SLA rakamları + "neler oldu, ne değişti, neye dikkat" anlatısı; e-posta ve panel. Toplu (batch) üretim. | A0 | P2 | M | Rapordaki her sayı DB'den hesaplanır, LLM sayı üretmez, yalnızca verilen sayıları yorumlar (test: anlatıdaki sayılar girdide var). | AI-0, FEAT-10 |
| AI-7 | **Doğal dilde soru-cevap.** "Geçen hafta en yavaş servisim hangisiydi?", "Ödeme API'si neden dün düştü?" Tenant kapsamlı salt-okunur araçlar (monitör listesi, istatistik, incident sorgusu) ile tool-use; yazma aracı yok. **Durum (uygulandı):** `/App/Ask` (Viewer dahil), `QuestionAnswerService` ayrı tool-use döngüsü (`IAiToolClient`, CI'da `FakeAiToolClient`), 5 salt-okunur araç (`QaToolbox`); müşteri ID'si yalnızca oturumdan, başka tenant ID'si "not_found". Monitör adları modele gitmez: model `[[monitor:ID]]` / `[[incident:ID]]` referansı verir, sayfa tenant kontrolüyle ada ve linke çevirir. Hata metinleri maskeli `*_untrusted` alanlarda. Soru başına tek `AiDecision`, döngüdeki her model çağrısı bütçeden ayrılır (`ModelCalls`); iterasyon / araç çağrısı / sonuç boyutu / soru uzunluğu / süre sınırları, kullanıcı başına hız sınırı (`Ai:Qa:*`). Sayı doğrulama kancası `IQaAnswerGrounding` (doğrulanamayan sayı uyarıyla gösterilir). Konuşma saklanmaz (yalnızca sayfa formunda). 8 eval senaryosu `evals/cases/question-answer`. | A0 | P3 | L | Araçlar tenant ID'yi oturumdan alır (test: başka tenant verisine erişim denemesi boş döner). Yanıtlar kaynak kayıtlara link verir. Viewer rolü kullanabilir. | AI-0 |
| AI-8 | **Postmortem ve status page taslağı.** Kapanan incident için zaman çizelgesi + etki + olası neden + müşteriye dönük açıklama taslağı. | A2 | P3 | M | Yayınlanmadan önce CustomerAdmin düzenler/onaylar; onaysız hiçbir şey yayınlanmaz. | AI-2, FEAT-4 |
| AI-9 | **Monitör kurulum asistanı.** "example.com'un ödeme sayfasını 1 dk'da izle, 'Ödeme' yazısı yoksa alarm" → doldurulmuş form. Plan kuralları ve SSRF doğrulaması normal formdan geçer. | A1 | P3 | S | Asistan hiçbir zaman doğrudan kaydetmez; formu doldurur. | AI-0, SEC-1 |
| AI-10 | **Plan bazlı AI kotası ve maliyet bütçesi.** Basic: yalnızca AI-2 (aylık sınırlı); Standard: + AI-3, AI-5; Pro: + AI-4, AI-6; Enterprise: + AI-7, AI-8. Tenant başına aylık token bütçesi; aşımda AI özellikleri sessizce A0'a / kapalıya düşer. | n/a | P2 | M | Bütçe aşımı ana akışı etkilemez; panelde kota görünür (BIZ-3). SuperAdmin'de tenant başına AI maliyeti. | AI-0 |
| AI-11 | **Değerlendirme (eval) seti ve kalite kapısı.** UAT test sunucularıyla (8765/8767/8799…) üretilen etiketli incident senaryoları + 👍/👎 verisi. Prompt, model veya effort değişikliği eval'den geçmeden yayına çıkmaz. Özerklik seviyesi yükseltmesi eval eşiğine bağlı. **Durum (uygulandı):** `evals/TriggerCheck.Evals` (sahte/gerçek mod, gerçek bağlam oluşturucu, kod tabanlı not verici, rapor `evals/reports/`). CI'da sahte istemciyle çalışır; kapı ve özerklik eşikleri `evals/README.md`'de. | n/a | P1 | M | CI'da (veya manuel tetiklemeyle) eval çalışır, skor raporlanır; eşik altı değişiklik birleştirilmez. | AI-0, AI-2 |
| AI-12 | **SuperAdmin içgörüleri.** Platform genelinde: en çok gürültü üreten müşteriler, plan sınırına dayanan müşteriler (yükseltme sinyali), sürekli DOWN monitörler (terk edilmiş hesap sinyali). | A1 | P3 | M | Sinyaller kural tabanlı hesaplanır; LLM yalnızca özetler. | AI-0, AI-1 |

### AI için açık kararlar

| # | Karar | Seçenekler | Öneri |
|---|-------|-----------|-------|
| K1 | Yüksek hacimli işler için model | Opus 5.5 (düşük effort) / Haiku 5.5 | Opus 5.5 ile başla; AI-11 eval'i Haiku'da kaliteyi tutuyorsa maliyet için geç. Karar ürün sahibine ait. |
| K2 | AI özellikleri varsayılan açık mı? | Açık (opt-out) / Kapalı (opt-in) | Mevcut müşteriler için opt-in, yeni müşteriler için açık; gizlilik metni güncellenir. |
| K3 | AI-4 gruplama A3 mi A1 mi başlasın? | A1 (öneri) / A3 (otomatik) | 4 hafta A1 ("bu 5 incident'ı gruplayalım mı?") olarak çalıştır, kabul oranı > %90 ise A3'e yükselt. |
| K4 | Bildirimde AI özeti | DOWN anında beklemeden / ayrı takip mesajı | Asla bekleme; DOWN bildirimi hemen, özet hazır olunca incident sayfasında ve (Slack gibi kanallarda) mesaj güncellemesi. |
| K5 | Veri yerleşimi / KVKK | Hangi verinin dışarı çıkabileceği | Hukuki görüş alınana kadar yalnızca maskelenmiş teknik veri (URL host, durum kodu, hata türü, süreler) gönderilir. |

---

## Önerilen sıralama

1. **Sprint 1 (üretim engelleri):** SEC-1, SEC-2, SEC-3, ENG-1, ENG-2, OPS-1
2. **Sprint 2 (dayanıklılık + redesign):** ENG-3, ENG-4, SEC-4, SEC-7, UI-1, UI-2, OPS-4
3. **Sprint 3 (değer + AI temeli):** ENG-8, ENG-9, FEAT-1, FEAT-2, AI-0, AI-1, AI-11
4. **Sprint 4 (ilk AI özellikleri):** AI-2, AI-3, UI-4, FEAT-3, OPS-2, OPS-5
5. **Sonrası:** AI-4, AI-5, AI-6, AI-10, FEAT-4, FEAT-5, BIZ-1/2, OPS-3 → P3 kalemler

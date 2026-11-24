# BackupForge – Windows mentőalkalmazás (handoff)

> Ez a fájl úgy készül, hogy egy nulla kontextussal induló másik Claude-példány (vagy fejlesztő) innen folytatni tudja a munkát. **Minden érdemi lépés (fázis) után frissíteni kell**: mi készült el, mi tért el a spec-től és miért, mi a következő lépés, mik a nyitott kérdések.

## Mi ez
Ingyenes, Windows-szolgáltatásként futó mentőalkalmazás KKV-szerverekre: FFS-szerű fájlmentés Versions mappákkal, restic-tárolós mentés, SQL Server-mentés, láncok, helyi e-mail-riasztás és heti riport. Egyetlen telepítő, WPF konfigurátor, `bf.exe` CLI.
**Az app ment, a felügyeletet több gépre a BackupPulse végzi** (külön repó: `Craftzone-IT/BackupPulse`) — az integráció egy verziózott állapotfájl: `docs/STATUS-CONTRACT.md`.

## Dokumentumok (ebben a sorrendben olvasd)
1. `docs/SPEC.md` — **a specifikáció** (mit, hogyan, fázisok a 17. pontban, nyitott kérdések alapértelmezéssel a 18. pontban).
2. `docs/STATUS-CONTRACT.md` — a `status.json` szerződés (BackupPulse felé). Mezőt törölni / jelentést változtatni csak `status_schema` növeléssel szabad.
3. `docs/DECISIONS.md` — döntésnapló indoklással (miért nem FFS-fork, miért restic és nem Kopia, stb.).
4. `docs/BUSINESS.md` — üzleti irány (ingyenes app + fizetős felügyelet); a v1 fejlesztésnek nem része, de a döntéseknél figyelembe veendő.
5. `docs/AGENT-RUN.md` — **felügyelet nélküli futtatás szabályai** (sorrend, döntések, tilalmak, CI, mit kell itt vezetni). Ha a feladatod „haladj felügyelet nélkül”, ez az irányadó.

## Munkamódszer
- A spec-et Claude (chat) írja, a végrehajtást **Claude Code** végzi, **fázisonként** (SPEC 17.). Egy fázis = egy vagy több commit a `main`-re; a fázis végén: tesztek zöldek, ez a fájl frissítve (Állapot + Eltérések), push.
- Ha a spec és a valóság ütközik, a végrehajtó a jobb megoldást választja és **itt dokumentálja** („Eltérések a spec-től” szakasz, fázisonként). Üzleti / felhasználói döntésnél kérdez.
- Új igény / változtatás a spec után: `docs/CR-NNN-<téma>.md` (change request), a chat írja; állapota a fájl elején („Állapot: NYITOTT / KÉSZ (dátum)”).
- Commit-üzenetek: angolul vagy magyarul, de tömören; fázisnál előtag: `F2: …`.

## Konvenciók
- Kód: C# / .NET 10 LTS, nullable bekapcsolva, warnings as errors a `src/`-ben. Azonosítók és kommentek angolul; felhasználói szövegek `Resources`-ben (hu + en); dokumentáció magyarul.
- A platformfüggetlen logika (Core, motorok tervező része) absztrakciókon keresztül (fájlrendszer, óra, folyamatindítás) érje el a rendszert, hogy a unit tesztek **Linuxon is** fussanak. Windows-specifikus kód csak a `BackupForge.Windows`-ban és a hostokban.
- Titok soha nem kerülhet naplóba, státuszba, e-mailbe, kivételszövegbe, parancssorba (restic: környezeti változó).
- Fájlműveletek a célon: csak a saját célmappában / Versions mappában / staging-ben töröl; minden törlő kódútnak legyen tesztje, ami bizonyítja, hogy idegen mappát nem bánt.
- PowerShell-szkriptek (ha lesznek, pl. telepítő-segéd): UTF-8 **BOM-mal** (PS 5.1 különben rosszul olvassa az ékezeteket).

## Állapot
- **F0 — KÉSZ (2026-11-24)** (a CI-eredmény a push után: lásd lent / a commit-történetet).
  - Solution: `BackupForge.slnx` (a .NET 10 SDK alapformátuma), `global.json` (10.0.100, `rollForward: latestFeature`), központi csomagverziók: `Directory.Packages.props`, közös beállítások: `Directory.Build.props` (nullable, `EnableWindowsTargeting` → a WPF is fordul Linuxon), `src/Directory.Build.props` (warnings as errors).
  - Projektek a SPEC 3. pontja szerint (`src/`: Core, Engine.Files, Engine.Restic, Engine.Sql, Windows, Ipc, Service, Cli (`bf`), Gui (WPF, `BackupForge.exe`); `tests/`: Core.Tests, Engine.Files.Tests, Engine.Restic.Tests, Integration.Tests + **`BackupForge.TestKit`** (közös: `TempDir`, `WindowsOnlyFact`/`WindowsOnlyTheory`/`WindowsAdminFact`).
  - `Core/Config`: `BackupConfig` (a teljes `config.json` modell, snake_case, `ConfigJson` source-generátorral; ismeretlen mező → `ConfigFormatException` a JSON-úttal), `ConfigValidator` (mezőszintű `ValidationError(path, code, args)`, üzenet `Texts`-ből hu/en), `JobPaths` (tükör- és Versions-útvonal).
  - `Core/Abstractions`: `IClock` (+ `ManualClock`), `IFileSystem` (+ `PhysicalFileSystem`), `IProcessRunner` (+ `SystemProcessRunner`, titok csak env-ben), `ISecretStore` (+ `InMemorySecretStore`), `PathUtil` (Windows-stílusú útvonalak OS-függetlenül).
  - `Core/Logging`: `SecretMasker` (ismert titkok + URL-jelszó + `password=` minták), `FileLoggerProvider` (napi forgatás `<prefix>-yyyyMMdd.log`, megőrzés napokban, csak a saját mintájú fájlokat törli).
  - `Core/Resources`: `Strings.resx` (en) + `Strings.hu.resx`; új szöveg: `python3 tools/resx.py src/BackupForge.Core/Resources <kulcs> "<en>" "<hu>"` (vagy `--batch <tsv>`).
  - `schema/config.schema.json` (Draft 2020-12, strukturális) + `schema/examples/config.example.json` (a SPEC példája). Teszt: a példa és a szerializált modell is megfelel a sémának (JsonSchema.Net).
  - `third_party/restic/VERSION` (0.20.0, a hash-ek a CI első futásából rögzítendők) + `LICENSE` (BSD-2); `tools/fetch_restic.py` (letöltés + ellenőrzés a hivatalos `SHA256SUMS` és a rögzített hash ellen).
  - CI: `.github/workflows/ci.yml` — `linux` (build + teszt) és `windows` (build + minden teszt), `concurrency` + `cancel-in-progress`.
  - Tesztek: 120 (Core) + 3 füstteszt; `dotnet test BackupForge.slnx` zöld helyben (Linux).

- **F1 — KÉSZ (2026-11-24)** (a Windows-szolgáltatás integrációs tesztje a CI Windows-runnerén fut).
  - Ütemező: `Core/Scheduling/ScheduleCalculator` (`Next` / `Previous`, interval / daily / weekly / monthly; DST: nem létező helyi idő → a váltás utáni első perc, ismétlődő óra → csak egyszer, az első előfordulás), `ExpectedInterval` (SPEC 4. szabályai, chained = a legnagyobb indító), `ScheduleText` (hu/en, magyar -tól/-től illesztéssel), `TimeSpec` (ÓÓ:pp, napok, „weekly Sun 03:00”-féle ismétlődések).
  - Végrehajtás: `Core/Execution` — `JobCoordinator` (sor, `max_parallel_jobs`, erőforrászárak `ResourceLocks` (célmappa / restic-tároló / SQL-példány), egy feladat egyszerre egyszer, ütemezett ütközés → `skipped (still running)`, letiltott automatikus futás → `skipped (disabled)`, időkorlát → `failed: timeout`, megszakítás → `interrupted`, leállítás 60 mp türelmi idővel, láncok `chain_run_id`-vel, hibánál `skipped (chain: <id> failed)`, dry-run nem indít láncot), `JobScheduler` (esedékesség, catch-up induláskor egyszer, 2 perc késleltetéssel — csak ha a feladatot már korábban is ismerte (`seen:<kulcs>` a kv-ben) és az utolsó esedékesség óta nem indult; restic forget/prune + check és SQL CHECKDB bejegyzések is), `IJobEngine` / `RunOutcome` / `JobRunContext`, `FileRunLog` (`logs\jobs\<job>\<run>.log`, maszkolva) + megőrzés, `DataPaths` (`BACKUPFORGE_DATA` felülírja).
  - Előzmények: `Core/History/HistoryStore` (SQLite, WAL; a SPEC 9.1 táblái + `kv` állapot-tábla; időpontok Unix ms UTC-ben). Induláskor a befejezetlen futások `interrupted`-ek.
  - Konfig: `ConfigStore` (betöltés + validálás, hibás fájlnál az utolsó érvényes marad és a hiba a státuszba kerül; mentés atomikusan, `config.json.bak.1..10`; minden változás `config_changes` + `ConfigDiff` összefoglaló, letiltás → `disabled_since`), `AtomicFile`.
  - Státusz-modell (az F3 előkészítése): `Core/Status/StatusDocument` (a szerződés mezői) + `StatusBuilder` (+ `Watchdog.IsStale`, `LocationDisplay`).
  - IPC: `src/BackupForge.Ipc` — sorvégjeles JSON a named pipe-on (`IpcServer` / `IpcClient`, protokollverzió minden kérésben, szerver-push események `events.subscribe` után). Windowson a pipe ACL-je SYSTEM + Administrators. Linuxon (Unix socket) is működik → a tesztek ott is futnak.
  - Szolgáltatás: `src/BackupForge.Service` — `ServiceRuntime` (host-független mag; `IServiceComponent` bővítési pont a későbbi fázisoknak), `ServiceIpcHandler` (status/config/secrets/job/history/log műveletek), `Worker` (5 mp-es tick), `Program` (`AddWindowsService`, leállítási időkorlát 75 mp, fájlnapló; kapcsolók: `--data`, `--pipe`, `--test-engine`), `TestEngine` (csak `--test-engine` / `BACKUPFORGE_TEST_ENGINE=1` mellett).
  - CLI: `src/BackupForge.Cli/BfCli` (`bf status [--json]`, `jobs`, `run [--dry-run] [--allow-mass-delete] [--wait]`, `cancel`, `history`, `log`, `config validate|reload|export|import`, `secret set`, …; kilépési kódok a SPEC szerint).
  - Tesztek: Core 162 (ütemező + DST, koordinátor, ütemező/catch-up, előzmények), `BackupForge.Service.Tests` 11 (in-process runtime + valódi pipe + CLI), `BackupForge.Integration.Tests`: a szolgáltatás valódi folyamatként + `bf` folyamat (Linuxon is), Windowson `sc create` → indítás → futtatás → leállítás → törlés (`WindowsAdminFact`).

- **F2 — KÉSZ (2026-11-24)**, CI zöld.
  - Fájlmotor: `src/BackupForge.Engine.Files` — `Planner` (bejárás + terv, `size_mtime` ± 2 mp / pontosan ± 1 óra azonos mérettel = változatlan (első esetnél INFO), `content` → xxHash64, kis-/nagybetűs átnevezés, a nem listázható forrásmappa alatt soha nincs törlés, reparse point nem követve), `FileCopier` (`.bfpart` → flush → régi a Versions-be → átnevezés → idők + attribútumok, Readonly levétele), `FileJobEngine` (cél-elérés, üres / hiányzó forrás → `failed`, VSS (`when_available` → warning, `required` → failed), törlési biztosíték a törlések ELŐTT (kv: `delete_guard_tripped:` / `delete_guard_approved:`; `--allow-mass-delete` vagy egyszeri jóváhagyás `bf approve-delete`), párhuzamos másolás 1–16, 3 újrapróbálás (1/5/15 mp), < 1 % és < 100 hiba → warning, különben failed, elveszett cél → azonnal failed, token-bucket `throttle_mbps`, dry-run (csak terv a futásnaplóba)), `VersionsRetention` (csak a minta szerinti `yyyy-MM-dd HHmmss` nevű mappák a saját Versions mappában; `max_versions_per_file`), `ExcludeMatcher` (minták + alapértelmezett kizárások).
  - Windows: `src/BackupForge.Windows` — `DpapiSecretStore` (`secrets.dat` = DPAPI LocalMachine + entrópia, ACL: SYSTEM (+ a futtató user, ha nem SYSTEM)), `WNetConnector` (WNetAddConnection2 meghajtóbetű nélkül, 1219 → érthető üzenet), `VssSnapshotProvider` (saját COM-interop a `vssapi.dll`-re: `CreateVssBackupComponentsInternal`, `VSS_BT_COPY`, writerekkel, auto-release; árva pillanatképek törlése induláskor), `EventLogSink` / `WindowsEventSink`.
  - Szolgáltatás-komponensek (`src/BackupForge.Service/Components`): `Notifier` (azonnali hiba / figyelmeztetés / helyreállás, 24 órás azonos-szöveg-összevonás, kimenő sor 1/5/15 perces újrapróbálással, utána 2020-as esemény + `notifications.last_error`; watchdog-levél (SPEC 10.2) 24 óránként; konfighiba-levél), `MailKitSender`, `TargetProbe` (5 percenként cél-elérés + szabad hely), `LifecycleEvents` (1000/1001, VSS-árvák). `docs/EVENT-IDS.md`.
  - Tesztek: Engine.Files 47 (10 000 fájl, újrapróbálás és célvesztés hibainjektáló `FaultyFileSystem`-mel, …), Service 20 (értesítések, SMTP-elkapóval a MailKit is), Integration (Windows): DPAPI, VSS zárolt fájllal, VSS-es fájlfeladat, > 260 karakteres út, attribútumok, eseménynapló, SMB-cél helyi megosztással és helyi userrel, telepített szolgáltatás valódi fájlfeladattal.
- **F3 — KÉSZ (2026-11-24)**, CI zöld.
  - `schema/status.schema.json` (a STATUS-CONTRACT 3. pontja alapján; a szerződés példája `schema/examples/status.example.json`, teszt validálja), `StatusWriter` (percenként + minden futás-állapotváltáskor, atomikusan, maszkolva, ~64 KB fölött rövidített hibalisták), `Heartbeat` (healthchecks-kompatibilis, az URL titokként kezelve), `WeeklyReport` + `WeeklyReporter` (ütemezés: `weekly_report`, kihagyott riport induláskor egyszer; minta: `docs/weekly-report-sample.html`, frissítés: `BF_WRITE_SAMPLES=1 dotnet test tests/BackupForge.Service.Tests`), `bf report preview`.
  - Konfigváltozás-napló: már F1-ben (`ConfigStore` + `ConfigDiff`), a heti riportban és a `config_changed` mezőben látszik.
- **F4 — KÉSZ (2026-11-24)**, CI zöld: `src/BackupForge.Engine.Restic` — `ResticBinary` (a `third_party/restic/VERSION` beágyazva, SHA-256 ellenőrzés; `BACKUPFORGE_RESTIC` = ellenőrizetlen fejlesztői felülírás), `ResticRunner` (titok csak env-ben: `RESTIC_REPOSITORY`, `RESTIC_PASSWORD`, `env_refs`), `ResticParser` (valódi 0.20.0 kimenetek a `tests/BackupForge.Engine.Restic.Tests/fixtures`-ben, útvonalak `/data`-ra cserélve), `ResticEngine` (backup + kilépési kódok 0/3/10/11/12/130, saját elavult zár → `restic unlock` + egy újrapróba, megszakításnál is unlock, forget --prune `--group-by host,tags`, check `--read-data-subset`, restore új mappába), `ResticClient` (init csak kifejezett kérésre, snapshots, ls, restore, version), `ResticComponent` (indulási hash- és verzió-ellenőrzés, `repo.*` IPC), `RestoreIpc` (`restore.run` a soron át, `Restore_<időbélyeg>` alá). Tesztek: 18 (fixture + valódi restic: init/backup/snapshots/ls/restore/forget/check, nincs tároló → nem hoz létre, rossz jelszó, exit 3, megszakítás). A Linux-CI a hivatalos linux_amd64 restic-kel (SHA256SUMS-szal ellenőrizve) futtatja a valódi restic-teszteket, a Windows-CI a rögzített `restic.exe`-vel — mindkettőn zöldek. `restic.exe` + `restic-LICENSE.txt` a szolgáltatás kimenetébe másolódik.
- **F5 — KÉSZ (2026-11-24)**, CI zöld (a LocalDB-teszt csak akkor fut, ha a runneren elérhető — a „SQL Server LocalDB” lépés sikeres volt; a teszt-eredmény a `test-results-windows` artifact trx-fájljában): `src/BackupForge.Engine.Sql` — `ISqlSession` / `SqlClientSession` (Microsoft.Data.SqlClient, Windows-auth = a folyamat identitása → a szolgáltatásnál `NT SERVICE\BackupForge`), `SqlPlanning` (adatbázis-választás, tömörítés `auto` (restic követi → nincs; Express → nincs), eredmény-összesítés (felhasználói db hiba → failed, csak rendszer-db → warning), FULL recovery figyelmeztetés (msdb `type='L'` 24 órán belül, `ignore_full_recovery`), `keep_local` takarítás (csak a saját névmintájú `.bak`-ok), T-SQL (`QUOTENAME`-szerű idézés), `GrantScript`), `SqlEngine` (szabad hely előellenőrzés, BACKUP → VERIFYONLY, sikertelen / nem ellenőrzött `.bak` törlése, CHECKDB), `IStagingPermissions` (+ `Windows/StagingAcl`). Szolgáltatás: `SqlSupport` (láncvég-takarítás: az `@staging:`-et utolsóként fogyasztó feladat sikere után; `sql` státuszblokk; `sql.discover`). CLI: `bf sql grant <jobId> [--login] [--yes]` helyben, a rendszergazda saját loginjával. `docs/SQL-PERMISSIONS.md`. Tesztek: Engine.Sql 15 (hamis munkamenet), Integration: LocalDB-n SQL → restic lánc + CHECKDB (csak ha a runneren van LocalDB).

- **F6 — KÉSZ (2026-11-24)**, CI zöld (build + ViewModel-tesztek; a GUI-folyamatok kézi tesztje: `docs/MANUAL-TESTS.md` C).
  - `src/BackupForge.Gui.Core` (net10.0, CommunityToolkit.Mvvm, WPF nélkül → Linuxon tesztelhető): `MainViewModel` (konfig + státusz + titok-lista betöltése, közös Mentés a szolgáltatás validálásával, mezőszintű hibák `ErrorsFor`), `OverviewViewModel` / `JobCardViewModel` (kártyák, haladás az IPC-eseményekből, Futtatás / Megszakítás / tömeges törlés egyszeri jóváhagyása, figyelmeztető sáv), `JobsViewModel` / `JobEditViewModel` (szerkesztő típusonként, ütemezés-előnézet, próbafuttatás; veszteségmentes oda-vissza alakítás tesztelve), `TargetsViewModel` (célok, restic-tárolók, jelszó csak írható, „Kapcsolat tesztelése” a szolgáltatás nevében, **új tároló varázsló**: 32 karakteres generált jelszó → `secrets.set` → egyszer megmutatott helyreállítási lap (nyomtatható) → mentés → csak megerősítés után `repo.init`), `SqlViewModel` (felderítés, „Jogosultságok beállítása” a GUI-ban, a rendszergazda saját loginjával, a T-SQL előbb megjelenik), `HistoryViewModel`, `RestoreViewModel` (pont → böngészés → cél → visszaállítás, eredeti hely csak megerősítéssel), `NotificationsViewModel`, `SettingsViewModel` (export / import, hiányzó titkok listája). Szövegek: `Resources/UiStrings(.hu).resx`.
  - `src/BackupForge.Gui` (WPF, `requireAdministrator` manifest): `MainWindow.xaml` 8 füllel (SPEC 11.2), `WpfDialogs` (üzenetek, mappa-/fájlválasztó, nyomtatható szövegablak), `{l:Loc kulcs}` markup-extension. A jelszómezők `PasswordBox`-ok, a kód-mögöttes rész közvetlenül `secrets.set`-be küldi és törli őket.
  - Visszaállítás (SPEC 13.): `Engine.Files/FileRestore.cs` — `FileRestoreEngine` (pontok: aktuális tükör + Versions-időbélyegek; új mappába `Restore_<időbélyeg>`, nem üres célba nem; eredeti helyre a felülírt fájlok `<forrás>_RestoreBackup_<időbélyeg>`-be; `..` tiltva), restic: `ResticEngine` restore-ja; IPC: `restore.versions` / `restore.browse` / `restore.run` (a soron át, `restore` futásként az előzményben). CLI: `bf restore`.
  - Tesztek: Gui 12 (hamis IPC-klienssel), Engine.Files +2 (visszaállítás). Kézi lista: `docs/MANUAL-TESTS.md` C. pont.
- **F7 — KÉSZ (2026-11-24)**, CI zöld a `dcbfdf9` commiton: a Windows-job publikál, telepítőt épít (artifact `installer`) és a füstteszt (csendes telepítés → szolgáltatás → `bf status` / `bf run` → eltávolítás) átment. A kézi tesztlista tiszta VM-en még hátra van (user / pilot).
  - `installer/BackupForge.iss` (Inno Setup 6, hu + en): admin, x64, Windows 10 / Server 2016+, licenc (`installer/license.txt` = MIT + restic BSD-2), programmappa, Start menü, opcionális PATH; telepítés után a `BackupForge.Service.exe --setup-install [--config] [--secrets]` segéd (konfig validálása és a helyére tétele, titkok importja a DPAPI-s `secrets.dat`-ba — a forrásfájlt nem törli, figyelmeztet —, ProgramData ACL, eseményforrás; napló: `C:\ProgramData\BackupForge\setup.log`), majd `sc create … start= delayed-auto`, `sc sidtype … unrestricted`, `sc failure … restart/60000 ×3, reset 86400`, indítás. Frissítés: a szolgáltatás leáll, a konfig / előzmények maradnak. Eltávolítás: kérdés „Megtartod a beállításokat?” (csendesen `/PURGE` kell a törléshez), `--setup-uninstall` (árva VSS, eseményforrás, `--purge` → csak a `ProgramData\BackupForge`), `sc delete`. Csendes: `/VERYSILENT /SUPPRESSMSGBOXES /CONFIG="…" [/SECRETS="…"]`.
  - `SetupHelper` (`src/BackupForge.Service/SetupHelper.cs`) + teszt.
  - CI (Windows): self-contained single-file publikálás (szolgáltatás, `bf`, GUI) → ISCC (ha nincs a runneren: `choco install innosetup`) → **füstteszt** `installer/smoke-test.ps1` (UTF-8 BOM): csendes telepítés konfiggal → a szolgáltatás fut, SID-típus UNRESTRICTED → `bf status` → `bf run docs --wait` → `status.json` létrejött → csendes eltávolítás → a szolgáltatás eltűnt, a célmappa érintetlen, a beállítások megmaradtak → artifact `installer`. Kiadás: `.github/workflows/release.yml` (`v*` tag → teszt, publikálás, telepítő, füstteszt, GitHub Release) — a taget a user hozza létre.
  - `LICENSE` (MIT, SPEC 18.2 alapértelmezés), `docs/EVENT-IDS.md`, `docs/MANUAL-TESTS.md`.

## Eltérések a spec-től (F0)
- **Ág**: az AGENT-RUN `main`-t ír, de ez a munkamenet a környezet előírása szerint a `claude/blissful-goldberg-vlt55t` ágra pusholhat (más ágra nem). Minden fázis ide megy; a `main`-be olvasztás (PR) a user dolga.
- `config.json`-ban az ismeretlen mező hiba (nem csendben eldobott) — egy elgépelt kulcs (pl. `enabeld`) ne kapcsoljon ki csendben semmit.
- A `versioning.folder` nem lehet a tükörmappán belül (a célgyökérbe mentő feladatnál sem relatív név): a tükör különben a saját Versions mappáját törölné / verziózná. Célgyökérbe mentésnél tehát abszolút, a tükrön kívüli út kell.
- Azonosítók (job / target / repo): `^[a-z0-9][a-z0-9_-]{0,63}$` (útvonalban és restic-tagben is szerepelnek).
- Hosszú útvonalak: a .NET (Core) Windowson magától `\\?\`-es útra vált, ezért a kód nem tesz elé előtagot; a > 260 karakteres teszt a Windows-runneren fut (F2).
- `BackupForge.Windows` `net10.0` célú (nem `net10.0-windows`), `[SupportedOSPlatform("windows")]`-szal, hogy a solution Linuxon is egyben forduljon; a hívó oldalon `OperatingSystem.IsWindows()` őrzi.

## Eltérések a spec-től (F1)
- `interval` ütemezés: a `start` egy folytonos sorozat horgonya (06:15 + 4 óra → 10:15, …, 22:15, 02:15), nem naponta újrakezdődő; a 1440-et osztó intervallumoknál ez minden nap ugyanazt az időpontsort adja. A `window` [from, to) — ha from > to, éjfélen átnyúlik.
- Catch-up csak akkor, ha a szolgáltatás a feladatot az elmulasztott esedékesség előtt már ismerte (különben egy új feladat a mentés után azonnal elindulna).
- Kézi futtatás futó / sorban álló feladatra: azonnali elutasítás (az előzménybe nem kerül), csak az automatikus (ütemezett / catch-up / lánc) ütközés kerül `skipped`-ként az előzménybe.
- Letiltott feladat: automatikusan nem fut (`skipped (disabled)`, ha lánc indítaná), kézzel futtatható.
- A restic-karbantartás (forget/prune, check) a koordinátorban `repo:<id>` azonosítójú munkaként fut, az előzményben is így látszik.
- `history.db`: + `kv` tábla (delete guard jóváhagyás, `seen:*`, `disabled_since:*`, riport-időpontok), `notifications.fingerprint`, `sql_db_results.recovery_model`, `vss_snapshots.set_id`.

## Eltérések a spec-től (F2)
- VSS: nem AlphaVSS (C++/CLI vegyes assembly, single-file kiadással nem tölthető be), hanem saját vékony COM-interop a `vssapi.dll`-re (`BackupForge.Windows/Vss.cs`). A pillanatképek auto-release kontextusban készülnek (a folyamat halálával is törlődnek); az `history.db` `vss_snapshots` táblája alapján induláskor a maradékot is törli.
- Hosszú útvonalak: nincs kézi `\\?\` előtag (a .NET magától kezeli), Windows-teszt van rá.
- Több forrás egy fájlfeladatban: mindegyik a tükör `<forrásmappa neve>` almappájába kerül (validálás: a nevek nem ismétlődhetnek). Egy forrásnál a tartalom közvetlenül a tükörbe.
- Kizárt fájlok a célon sem törlődnek (mirror módban sem) — biztonságosabb (FFS-szerű) viselkedés.
- VSS-writerek: ha a `GatherWriterMetadata` 90 mp alatt nem fejeződik be (a hosztolt Windows-runneren egy writer rendszeresen nem válaszolt), a pillanatkép egy friss requesterrel **writerek nélkül** (crash-konzisztens) készül; az aszinkron VSS-lépések felső korlátja 3 perc. A VSS-tesztek egy xUnit-gyűjteményben futnak (egyszerre egy snapshot-készlet). Pilotnál ellenőrizendő, hogy valós szervereken a writerek rendben válaszolnak-e (`vssadmin list writers`).
- Sorrend a végrehajtásban: kis-/nagybetűs átnevezés → törlések (helyfelszabadítás, a Versions-be) → mappák → másolások.
- Ha a futás közben eltűnik egy forrásfájl, az figyelmeztetés (nem hiba).
- `interrupted` futásról nem megy e-mail (leállítás / kézi megszakítás); a `failed` → `ok` helyreállás-levelet küld, a `warning` → `ok` is.
- E-mail-kimenő sor memóriában (szolgáltatás-újraindításkor az el nem küldött levél elvész; a `notifications` táblában `pending` / `retrying` marad nyomként).
- `TargetProbe`: az első ellenőrzésig (indulás után pár mp) a cél `online: false, checked: null` — a heti riport ezt „?”-ként, nem hibaként mutatja.

## Eltérések a spec-től (F3)
- A `status.json` `jobs[].target` / `versions` / `delete_guard` csak `files`, a `sql` csak `sql`, a `restic` csak `restic` feladatnál szerepel (a szerződés szerint a nem alkalmazható blokk hiányozhat vagy `null`); a még adat nélküli SQL-feladatnál üres `sql` blokk van.
- A `versions.size_gb` mindig `null` (a méret bejárása drága; a riport a mappák számát és a legrégebbit mutatja).
- `heartbeat_url`: sikeres futás után és óránként `GET <url>`, hibás futás után `GET <url>/fail`.

## Eltérések a spec-től (F5)
- Becsült mentési méret (szabadhely-ellenőrzés): az adatbázis utolsó teljes mentésének mérete az msdb-ből, ha nincs: a lefoglalt adatfájl-méret (felülbecslés). A „használt lapok” lekérdezéséhez db-szintű jog kellene.
- A staging-ACL-hez a példány alapértelmezett virtuális fiókja (`NT SERVICE\MSSQLSERVER` / `NT SERVICE\MSSQL$<NÉV>`), ha a `sys.dm_server_services` nem olvasható (ehhez `VIEW SERVER STATE` kellene — nem adjuk meg).
- „Jogosultságok beállítása”: a szkriptet a GUI / `bf sql grant` a rendszergazda saját identitásával futtatja (a szolgáltatás LocalSystem, nem tudná az admin hitelesítését használni); `sql.grant` IPC-művelet ezért nincs.
- `keep_local` takarítás: lánc nélkül az SQL-futás után, lánccal az `@staging:`-et utolsóként fogyasztó feladat sikeres vége után; csak a `<db>_yyyyMMdd_HHmmss.bak` mintájú fájlok, csak a feladat saját staging-mappájának gyökerében.
- Sikertelen BACKUP / VERIFYONLY után a félkész `.bak` törlődik (ne tűnjön mentésnek).
- `checkdb` csak felhasználói adatbázisokra (a SPEC szerint), saját `checkdb` futásként; eredménye a kv-ben (`last_checkdb`, `last_checkdb_ok` a státuszban).

## Eltérések a spec-től (F6)
- A GUI ViewModel-jei külön projektben (`BackupForge.Gui.Core`) vannak, hogy a tesztek Linuxon is fussanak.
- Fájlmentés-visszaállításnál a „pont” vagy az aktuális tükör, vagy egy Versions-időbélyeg mappa tartalma (az adott futáskor felülírt / törölt fájlok) — nem teljes „állapot X időpontban” rekonstrukció.
- A Próbafuttatás a MENTETT konfigurációval fut (a szolgáltatás oldalán); nem mentett változásnál a GUI rákérdez.
- A GUI egyszerű, mezőkből álló szerkesztő (nincs külön lépésenkénti varázsló a feladatokhoz); az ütemezés szöveges előnézete élőben frissül.

## Eltérések a spec-től (F7)
- Az eseménynapló-forrást és az ACL-eket a telepítő a szolgáltatás exe `--setup-install` parancsával állítja be (egy kódút a csendes és az interaktív telepítéshez, tesztelhető).
- Kiadásnál a verzió csak numerikus lehet (`v1.2.3`), mert az Inno `VersionInfoVersion` ezt követeli.
- Frissítéskor a konfigséma-migráció: a v1-ben egyetlen séma van; régebbi / ismeretlen sémánál a fájl `config.json.bak.premigrate` néven megmarad és figyelmeztetés kerül a `setup.log`-ba.

## Következő lépés
- **Minden fázis (F0–F7) kész, a CI zöld (Linux + Windows, telepítő-füstteszttel együtt).** Ami hátra van, az nem automatizálható itt:
  1. a `claude/blissful-goldberg-vlt55t` ág beolvasztása a `main`-be (PR, user);
  2. a `docs/MANUAL-TESTS.md` végigvitele egy tiszta Windows Server 2019/2022 VM-en (a 16. pont elfogadási feltételei: GUI-folyamatok, valódi NAS/SMB, SQL Express jogosultságok, e-mailek Outlookban, újraindítás futás közben);
  3. kiadás: `v0.1.0`-szerű tag (user) → `release.yml`;
  4. F8 (BackupPulse-integráció) a másik repóban, a chat CR-je alapján.
- Ismert teendők / javaslatok a pilot előtt: VSS-writer-viselkedés valós szerveren (lásd Eltérések F2); a GUI-szerkesztő bővítése varázslókká (jelenleg mezős szerkesztő); a `versions.size_gb` számítása (most `null`); e-mail-kimenő sor perzisztálása.

## Döntésre vár (user)
- **Ág**: minden munka a `claude/blissful-goldberg-vlt55t` ágon van (a környezet csak ide engedett pusholni) — a `main`-be olvasztás (PR) a user döntése.
- Licenc: MIT (SPEC 18.2 alapértelmezés) — a `LICENSE` a repó gyökerében.
- Végleges név: maradt a BackupForge (SPEC 18.1; átnevezés: namespace + telepítő AppId/név + dokumentáció).
- Kódaláírás: nincs (SPEC 18.3) — a telepítő és az exe-k aláíratlanok.
- .NET 10 és Windows Server 2016: a telepítő `MinVersion=10.0.14393` (Server 2016); a .NET 10 támogatási mátrixát a pilot előtt ellenőrizni kell (ha a 2016 kiesik, `MinVersion` emelése).
- restic-verzió: **0.20.0** (2026-11-19, a legfrissebb stabil; biztonsági javításokat tartalmaz). Ha konzervatívabb kell: 0.19.1.

## CI-ben / kézzel ellenőrizendő
- Windows-runneren (CI, zöld): VSS (writer-nélküli tartalékkal), DPAPI, eseménynapló, > 260 karakteres utak, telepített szolgáltatás valódi fájlfeladattal, restic.exe-vel minden restic-teszt, LocalDB-s SQL → restic lánc (csak ha a runneren van LocalDB — a „SQL Server LocalDB” CI-lépés kimenete mutatja), telepítő-füstteszt.
- Kézzel (`docs/MANUAL-TESTS.md`): GUI-folyamatok, SMB-hitelesítés valódi NAS-on, hálózati cél megszakadása, szolgáltatás- és gép-újraindítás futás közben, e-mailek Outlookban, SQL Express jogosultságai (ha a CI-ban nem volt LocalDB), telepítés / frissítés / eltávolítás tiszta VM-en.

## Ismert buktatók
- A konténerből a `github.com` (más repók, release-letöltés) nem érhető el → a restic forrását / dokumentációját a Go-proxyból lehet letölteni (`https://proxy.golang.org/github.com/restic/restic/@v/v0.20.0.zip`), a Windows-binárist csak a CI tölti le.
- `dotnet` helyben: `~/.dotnet` (dotnet-install.sh, 10.0 csatorna); `export PATH=$HOME/.dotnet:$PATH`.
- Helyi restic a valódi restic-tesztekhez: a Go-proxyból letöltött forrásból `GOTOOLCHAIN=auto go build ./cmd/restic` (go1.26 kell; a Go-eszközláncot a proxy adja), majd `BACKUPFORGE_RESTIC=<út> dotnet test`. A CI-ban a `tools/fetch_restic.py` (Windows: rögzített hash; Linux `--linux`: a hivatalos SHA256SUMS szerint) tölti le; `BF_REQUIRE_RESTIC=1` mellett a hiányzó bináris hiba, nem kihagyás.
- MailKit 5.0 API-ja eltér (SendAsync) → 4.18.2 maradt.
- `JsonValue.GetValue<long>()` kivételt dob, ha a csomópont `int` → a GUI-ban `J.L()` / `J.D()` tűrő olvasók; `async void` eseménykezelőben a kivétel az egész folyamatot leállítja → try/catch.
- Egy futó CI-t a következő push megszakít (`cancel-in-progress`) — hibakeresésnél a push előtt érdemes megvárni az eredményt.
- A Windows-CI hosszú (a 10 000 fájlos teszt ~1–2 perc): a `--blame-hang-timeout 6m` megállítja a beragadt tesztet.
- SMB-teszt a runneren: a helyi felhasználónak a megosztás mögötti mappára NTFS-jog is kell (`icacls … /grant`), különben „Access denied”; `\\127.0.0.1`-gyel 86-os hiba jött, `\\localhost`-tal működik.
- A hosztolt Windows-runneren egy VSS-writer nem válaszol a `GatherWriterMetadata`-ra → writer nélküli tartalék (lásd Eltérések F2). Egy korai, saját `CoInitializeSecurity` (IDENTIFY + dynamic cloaking) rontott rajta — maradt a lusta inicializálás.
- A 10 000 fájlos teszt a Windows-runneren ~2 perc (a felső korlát 10 perc).

## Nyitott kérdések
- Lásd SPEC 18. (név, licenc, kódaláírás, .NET 10 + Server 2016, GUI-keretrendszer, ügyfél-nézet, FFS/Cobian-importáló). Mindegyikhez van alapértelmezés, ezekkel lehet haladni.

## Kapcsolódó: BackupPulse
- Repó: `Craftzone-IT/BackupPulse` (PHP szerver + PowerShell / bash agentek). Ott van a meglévő FFS / Cobian / Veeam / sql_backup figyelés és a valós ügyféladatokból szerzett tapasztalat (FFS-lognevek, Cobian-napló, SQL-mentő script hibái) — érdemes elolvasni a `CLAUDE.md`-jét, ha egy viselkedés (pl. mit lát a SYSTEM-fiók, PS 5.1 buktatók) kérdéses.
- Az integráció (F8) a BackupPulse repóban egy külön CR lesz (`CR-010-backupforge.md` vagy a következő szabad szám), a STATUS-CONTRACT 4. pontja alapján.

# BackupForge — specifikáció (v1)

> Munkanév: **BackupForge** (a repó és a kód neve; később átnevezhető).
> Munkamódszer: ezt a specifikációt Claude (chat) írja, a végrehajtást **Claude Code** végzi fázisonként (17. pont). Minden fázis végén: tesztek zöldek, `CLAUDE.md` frissítve (elkészült / eltérések / nyitott kérdések), commit + push a `main`-re.
> Ha a spec és a valóság ütközik (pl. egy API másképp viselkedik), a végrehajtó a jobb megoldást választja, és **az eltérést a `CLAUDE.md`-ben dokumentálja**. Ha a döntés üzleti / felhasználói jellegű, kérdez.

## 1. Cél és hatókör

Ingyenes, Windows-szolgáltatásként futó mentőalkalmazás kis- és középvállalati szerverekre és munkaállomásokra. Tudásban a Cobian Reflector / FreeFileSync kategória, kiegészítve SQL Server-mentéssel és beépített felügyelettel. **Az app ment, a felügyeletet több gépre a BackupPulse végzi** (lásd `STATUS-CONTRACT.md`); az app egyedül is teljes értékű (helyi e-mail-riasztás, heti riport).

### Benne van (v1)
- Egyetlen telepítő (`setup.exe`), csendes telepítés előre elkészített konfigurációval.
- Windows-szolgáltatás saját ütemezővel (nem a Windows Feladatütemezőt használja).
- Feladattípusok, **párhuzamosan is futtathatók**:
  1. **Fájlmentés** (FFS-szerű): tükör / frissítés, `Versions` mappákkal, megőrzési idővel — saját C# motor.
  2. **restic-tároló**: verziózott, deduplikált, titkosított mentés — a hivatalos `restic.exe` becsomagolva.
  3. **SQL Server-mentés**: `BACKUP DATABASE` + ellenőrzés, átmeneti mappába; láncban követi egy fájl- vagy restic-feladat.
- VSS (nyitott fájlok), hosszú útvonalak, hálózati célok saját hitelesítéssel (meghajtóbetű nélkül).
- Láncok: egy feladat sikere indít egy másikat (pl. SQL → restic).
- Előzmények (SQLite), napló, `status.json` (BackupPulse-nak), Windows Eseménynapló.
- E-mail: azonnali hiba, helyreállás, heti HTML-riport.
- Konfigurátor GUI (WPF), parancssori eszköz (`bf.exe`).
- Visszaállítás: Versions mappából és restic-snapshotból, alapból más helyre.
- Magyar és angol felület (a szövegek erőforrásfájlban).

### Nincs benne (v1)
- Bare-metal / image-mentés, boot-média.
- MySQL / MariaDB / PostgreSQL (később `mysqldump`-pal, ugyanebbe a láncmodellbe).
- SQL-naplómentés (BACKUP LOG) és differenciális mentés — v1-ben csak teljes mentés + figyelmeztetés FULL recovery modelnél (8.4).
- Felhőcél saját implementációval (a restic által támogatott célok — SFTP, S3, rest-server — viszont mennek).
- Kétirányú szinkron, átnevezés-felismerés (v2).
- NTFS ACL-ek másolása a fájlmentésben (v2, opcióként).
- Automatikus önfrissítés.
- Windows Server 2012 R2 és régebbi, 32 bites Windows.

### Célplatform
- Windows 10 / 11, Windows Server 2016 / 2019 / 2022 / 2025, x64. (A .NET verzió támogatási mátrixát a végrehajtó ellenőrizze; ha a Server 2016 kiesik, jelezze.)
- SQL Server 2014+ (Express is). Régebbi példány: best effort.

## 2. Technológia

- **C# / .NET 10 (LTS)**. A .NET 8 támogatása 2026 novemberében lejár, ezért ne arra építsünk. Kiadás: `win-x64`, **self-contained**, single-file, ahol lehet (a szolgáltatásnál és a `bf.exe`-nél igen; a WPF-nél is, ha működik — ha nem, framework-dependent + a telepítő hozza a runtime-ot; eltérés → CLAUDE.md).
- Szolgáltatás: `Microsoft.Extensions.Hosting` + `UseWindowsService()`.
- Konfiguráció: JSON (`System.Text.Json`, source generator), saját validátor + JSON Schema fájl a repóban (`schema/config.schema.json`).
- Előzmények: SQLite (`Microsoft.Data.Sqlite`).
- SQL Server: `Microsoft.Data.SqlClient`.
- VSS: `AlphaVSS` (vagy egyenértékű, karbantartott csomag; ha nincs .NET 10-kompatibilis, saját vékony P/Invoke-réteg a `vssapi.dll`-re — a döntést a végrehajtó dokumentálja).
- E-mail: `MailKit`.
- GUI: WPF (.NET 10).
- IPC: named pipe (`System.IO.Pipes`), JSON-üzenetekkel.
- Naplózás: `Microsoft.Extensions.Logging` + fájl-sink (napi forgatás, 30 nap), eseménynapló-sink (csak Warning+).
- Telepítő: **Inno Setup** (szkriptelhető, CI-ban építhető Windows-runneren). Ha a végrehajtó jó okkal WiX-et választ, dokumentálja.
- Tesztek: xUnit. A platformfüggetlen logika (ütemező, összehasonlítás, megőrzés, konfig-validálás, restic-JSON-feldolgozás, státuszépítés) **Linuxon is futtatható** legyen absztrakciókon keresztül (fájlrendszer-interfész, óra-interfész). A Windows-függő részek (VSS, szolgáltatás, SQL, hálózati csatolás) integrációs tesztjei Windows-runneren futnak.
- Külső bináris: `restic.exe` — rögzített verzió + SHA-256 a `third_party/restic/VERSION` fájlban; a build letölti és ellenőrzi. Licencfájl (`LICENSE` BSD-2) a telepítőbe és a programmappába.
- Kódnyelv: angol (azonosítók, kommentek). Felhasználói szövegek: `Resources` (hu, en). Dokumentáció: magyar.

## 3. Komponensek és mappaszerkezet

```
src/
  BackupForge.Core/          # konfigmodell, validálás, ütemező, feladatsor, láncok, előzmények, státusz, értesítés-logika (platformfüggetlen)
  BackupForge.Engine.Files/  # fájlmentés-motor (összehasonlítás, másolás, Versions, megőrzés)
  BackupForge.Engine.Restic/ # restic.exe vezérlése, JSON-feldolgozás
  BackupForge.Engine.Sql/    # SQL Server-mentés
  BackupForge.Windows/       # VSS, hálózati csatolás (WNet), DPAPI, eseménynapló, szolgáltatás-segédek
  BackupForge.Service/       # Windows-szolgáltatás (host)
  BackupForge.Ipc/           # named pipe protokoll (közös a szolgáltatás, a GUI és a CLI között)
  BackupForge.Cli/           # bf.exe
  BackupForge.Gui/           # WPF konfigurátor
tests/
  BackupForge.Core.Tests/
  BackupForge.Engine.Files.Tests/
  BackupForge.Engine.Restic.Tests/   # rögzített restic --json kimenetekkel (fixtures/)
  BackupForge.Integration.Tests/     # csak Windows
installer/                   # Inno Setup szkript, csendes telepítés
schema/                      # config.schema.json, status.schema.json
third_party/restic/          # VERSION (verzió + sha256), LICENSE
docs/
```

### Futásidejű helyek
- Program: `C:\Program Files\BackupForge\` (`BackupForge.Service.exe`, `BackupForge.exe` (GUI), `bf.exe`, `restic.exe`, licencek).
- Adat: `C:\ProgramData\BackupForge\`
  - `config.json` (+ `config.json.bak.<n>`, az utolsó 10 mentett változat)
  - `secrets.dat` (DPAPI LocalMachine + entrópia; jelszavak, tokenek — 14. pont)
  - `history.db` (SQLite)
  - `status\status.json` (BackupPulse-nak — `STATUS-CONTRACT.md`)
  - `logs\service-YYYYMMDD.log`, `logs\jobs\<jobId>\<runId>.log` (futásonkénti részletes napló)
  - `staging\` (SQL `.bak`-ok, átmeneti fájlok)
  - `restic-cache\` (restic cache)
- ACL a `C:\ProgramData\BackupForge`-on: öröklés nélkül, SYSTEM + Administrators teljes; Users: **semmi** (a naplókban fájlnevek vannak). A `secrets.dat`: csak SYSTEM.

### Szolgáltatás
- Név: `BackupForge`, megjelenített név: „BackupForge mentés”. Indítás: automatikus (késleltetett).
- Fiók: **LocalSystem** (VSS, SeBackupPrivilege, minden fájl olvasása). Service SID: `unrestricted` (`sc sidtype BackupForge unrestricted`), hogy az SQL-jogosultság a `NT SERVICE\BackupForge` fiókra adható legyen (8.6).
- Helyreállítás (SCM recovery): 1., 2., további hiba → újraindítás 1 perc után; a hibaszámláló 1 nap után nullázódik.
- Leállításkor: a futó feladatok kapnak megszakítási jelet, 60 mp türelmi idő (SCM `RequestAdditionalTime`), utána a futás `interrupted`.

## 4. Konfiguráció (`config.json`, `schema: 1`)

Egyetlen fájl; a szolgáltatás olvassa, a GUI/CLI csak IPC-n keresztül módosítja (a szolgáltatás validál, ment, és `config.json.bak.<n>`-t készít). Kézi szerkesztés után: `bf config reload` vagy szolgáltatás-újraindítás; érvénytelen konfignál a szolgáltatás az utolsó érvényeset használja tovább, és CRIT eseményt + e-mailt ad.

Titkok **soha** nincsenek a `config.json`-ban: csak hivatkozás (`"password_ref": "target:nas1"`), az érték a `secrets.dat`-ban.

```json
{
  "schema": 1,
  "instance_name": "SRV01 mentés",
  "language": "hu",
  "general": {
    "max_parallel_jobs": 2,
    "history_retention_days": 400,
    "log_retention_days": 30
  },
  "targets": [
    { "id": "nas1", "type": "smb", "path": "\\\\192.168.1.10\\backup\\srv01", "username": "nas\\backupuser", "password_ref": "target:nas1", "min_free_gb": 50 },
    { "id": "usb", "type": "local", "path": "X:\\Mentes", "min_free_gb": 20 }
  ],
  "restic_repos": [
    { "id": "offsite", "location": "sftp:backup@storage.example.com:/srv01", "password_ref": "restic:offsite",
      "env_refs": { }, "append_only_server": false,
      "retention": { "keep_last": 3, "keep_daily": 14, "keep_weekly": 8, "keep_monthly": 12, "keep_yearly": 2 },
      "maintenance": { "forget_prune": "weekly Sun 03:00", "check": "weekly Sat 05:00", "check_read_data_subset": "5%" } }
  ],
  "jobs": [
    {
      "id": "company-mirror", "name": "Céges adatok → NAS", "type": "files", "enabled": true,
      "schedule": { "kind": "interval", "every_min": 240, "start": "06:15", "window": null },
      "sources": [ { "path": "D:\\Company", "excludes": ["*.tmp", "~$*", "Thumbs.db", "\\$RECYCLE.BIN\\"] } ],
      "target": { "target_id": "nas1", "subdir": "Company" },
      "files": {
        "mode": "mirror",
        "versioning": { "enabled": true, "folder": null, "keep_days": 21, "max_versions_per_file": null },
        "vss": "when_available",
        "delete_guard_pct": 20,
        "delete_guard_min_files": 50,
        "compare": "size_mtime"
      },
      "expected_interval_h": 4,
      "notify": "default"
    },
    {
      "id": "sql-main", "name": "SQL: SQLEXPRESS", "type": "sql", "enabled": true,
      "schedule": { "kind": "daily", "times": ["12:30", "22:30"] },
      "sql": {
        "instance": ".\\SQLEXPRESS", "auth": "windows", "login_ref": null,
        "databases": { "mode": "all_user", "include": [], "exclude": [], "include_system": true },
        "verify": true, "copy_only": false, "compression": "auto",
        "keep_local": 2, "checkdb": "weekly Sun 01:00"
      },
      "on_success": ["sql-to-offsite"]
    },
    {
      "id": "sql-to-offsite", "name": "SQL-mentések → offsite", "type": "restic", "enabled": true,
      "schedule": { "kind": "chained" },
      "sources": [ { "path": "@staging:sql-main" } ],
      "restic": { "repo_id": "offsite", "vss": "never", "tags": ["sql"] }
    }
  ],
  "notifications": {
    "email": {
      "enabled": true, "smtp_host": "smtp.office365.com", "smtp_port": 587, "security": "starttls",
      "username": "system@example.com", "password_ref": "smtp", "from": "system@example.com",
      "to": ["it@example.com"],
      "on_failure": true, "on_warning": true, "on_recovery": true,
      "weekly_report": "Sun 06:30"
    },
    "event_log": true,
    "heartbeat_url": null
  }
}
```

### Ütemezés (`schedule.kind`)
- `interval`: `every_min` (≥ 5), első futás `start` (HH:mm, helyi idő), opcionális `window` (`{"from":"06:00","to":"22:00"}`, azon kívül nem indít).
- `daily`: `times[]` (HH:mm), opcionális `days` (`["Mon","Tue",…]`).
- `weekly`: `day` + `time`.
- `monthly`: `day_of_month` (1–28 vagy `last`) + `time`.
- `chained`: csak lánc indítja (vagy kézzel).
- `manual`: csak kézzel.
- Minden időpont **helyi idő** szerint értendő; a nyári/téli időszámítás váltásakor kimaradó óra időpontja a váltás utáni első percben fut, az ismétlődő óra csak egyszer (teszt kell rá).

### `expected_interval_h`
Opcionális; ha nincs megadva, az ütemezésből számolódik (interval: `every_min/60`; daily: a napon belüli időpontok közti **legnagyobb** rés, a napváltáson átívelőt is beleértve — pl. 12:30 és 22:30 → 14; weekly: 168; monthly: 744). `chained` feladat az őt indító feladat értékét örökli (több indító esetén a legnagyobbat); `manual` feladatnál nincs (nem „stale”-elhet, csak ha kézzel megadják). Ebből számol a „nincs friss sikeres mentés” szabály (10.2) és a BackupPulse.

### Validálás (mentés előtt kötelező)
Egyedi azonosítók; létező hivatkozások (target_id, repo_id, on_success); lánc nem lehet körkörös; `@staging:<jobId>` csak SQL-feladatra mutathat; ugyanarra a célmappára (target + subdir) két `files`-feladat nem írhat; forrás és cél nem lehet egymásban; `delete_guard_pct` 1–100; a `password_ref`-ekhez létező titok. Hibánál mezőszintű, magyar/angol hibaüzenet (GUI-ban a mező mellett).

## 5. Végrehajtás: feladatsor, párhuzamosság, láncok

- **Feladatsor** a szolgáltatásban. Globális limit: `max_parallel_jobs` (alap 2).
- **Erőforrászárak** (a sorból csak akkor indul egy feladat, ha minden zárat megkap):
  - célzár: `files`-feladatnál `target_id + subdir` (kizárólagos);
  - tárolózár: restic-tárolónként egy író (backup), a `forget/prune` és a `check` kizárólagos;
  - SQL-példányzár: példányonként egy mentés egyszerre.
- Ugyanaz a feladat nem fut kétszer párhuzamosan; ha esedékes, miközben fut: **kihagyja**, és ezt az előzményben `skipped (still running)`-ként rögzíti (nem hiba, de a heti riportban látszik).
- **Elmaradt futás** (a gép ki volt kapcsolva / a szolgáltatás nem futott): induláskor, ha egy feladat utolsó esedékessége elmúlt és azóta nem futott, **egyszer** lefut (catch-up), 2 perc késleltetéssel az indulás után. Konfigurálható feladatonként: `"catch_up": true` (alap).
- **Láncok**: `on_success: [jobId…]` — a lánc tagja akkor indul, ha az előző `ok` vagy `warning`. `failed` esetén a lánc megáll; a láncolt feladat előzménybe `skipped (chain: <jobId> failed)` kerül. A lánc egy „láncfutás-azonosítót” (`chain_run_id`) kap, a státuszban és a riportban egybe látszik.
- **Időkorlát** feladatonként (`timeout_min`, alap: files 720, restic 1440, sql 360). Túllépés → megszakítás, `failed: timeout`.
- **Kézi futtatás**: GUI / `bf run <jobId>` — ugyanazon a soron és zárakon megy át.
- **Megszakítás**: GUI / `bf cancel <jobId>` — a motor kooperatívan áll le (fájl határon; restic-nél a folyamat leállítása, utána `restic unlock` csak a saját lockra; SQL-nél a parancs megszakítása).
- **Futás eredménye** (`result`): `ok` | `warning` | `failed` | `interrupted` | `skipped`. Minden futásnál: kezdés, vége, időtartam, feldolgozott/másolt/törölt/verziózott fájlok, bájtok, hibák (az első 50 részletesen, a többi összeszámolva), összegző szöveg (`summary`, egy sor, emberi).

## 6. Fájlmentés-motor (`type: files`)

### 6.1 Módok
- `mirror`: a cél pontosan a forrás tükre. A célon lévő, forrásban nem létező fájlok/mappák → Versions mappába (ha a verziózás be van kapcsolva), különben törlés.
- `update`: csak új és módosult fájlok másolása, a célon semmi nem törlődik (felülírás előtt a régi → Versions).

### 6.2 Összehasonlítás
- `size_mtime` (alap): eltér, ha a méret vagy az utolsó módosítás ideje eltér.
  - **Időtűrés**: 2 mp (FAT/exFAT célnál és SMB-n is előfordul), és **pontosan ±1 óra eltérésnél** (DST / FAT helyi idő) azonos méret esetén nem tekintjük módosultnak — de az első ilyen esetnél a futásnaplóba egy INFO sor kerül.
  - Időbélyegeket UTC-ben hasonlítunk.
- `content` (opcionális): kétes esetben (méret egyezik, idő eltér) tartalom-hash (xxHash64). Lassú, csak kérésre.
- Kis- és nagybetű: a Windows-fájlrendszer nem különbözteti meg; ha csak a név kis-/nagybetűje változott, a célon átnevezés történik (nem törlés + másolás).

### 6.3 Másolás
- Fájlonként: írás `<cél>.bfpart` ideiglenes névre → `FlushFileBuffers` → (ha a régi létezik és verziózás van: a régi áthelyezése a Versions-be) → átnevezés a végső névre. Félbemaradt futás után a `.bfpart` fájlokat a következő futás törli (csak a saját célmappájában).
- Megőrizzük: tartalom, utolsó módosítás ideje, létrehozás ideje, attribútumok (Readonly, Hidden, System, Archive — a cél Readonly-t a felülíráshoz átmenetileg levesszük). ACL: nem (v1).
- Mappák: létrehozás és törlés (üres mappák is tükröződnek mirror módban).
- Hosszú útvonalak: minden fájlművelet `\\?\` / `\\?\UNC\` előtaggal; 260 karakter feletti útvonalakra teszt kell.
- Speciális esetek: reparse pointok (symlink, junction) **nem követjük**, csak naplózzuk (INFO); OneDrive „csak felhőben” (Files On-Demand, `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `RECALL_ON_OPEN`) fájlok: alapból kihagyjuk és WARN-ként naplózzuk (a SYSTEM-fiók amúgy sem tudja előhívni) — feladatonkénti opcióval (`"cloud_files": "skip" | "fail"`).
- Hibakezelés fájlonként: 3 újrapróbálás (1, 5, 15 mp), utána a fájl kimarad, a futás `warning` (ha a kimaradt fájlok aránya < 1% és < 100 db), különben `failed`. Hálózati megszakadás (a cél elérhetetlen) → a futás azonnal `failed`.
- Párhuzamosság a motoron belül: alapból 4 párhuzamos másolás (konfigurálható 1–16), hálózati célnál is.
- Sávszélesség-korlát: opcionális (`"throttle_mbps"`), v1-ben egyszerű token-bucket.

### 6.4 Versions mappa
- Hely (**döntés**): a feladat célmappája **mellett**: `<target>\<subdir>_Versions\` — így a tükörben nincs idegen mappa, és ugyanez a minta, mint az ügyfeleknél futó FFS-telepítéseknél (`X:\…_FFS` + `X:\…_Versions`). A `versioning.folder` felülírhatja (abszolút út, vagy a célgyökérhez relatív név). Ha a `subdir` üres (a feladat a célgyökérbe ment), a `versioning.folder` megadása kötelező (validálás).
- Szerkezet: `<Versions>\YYYY-MM-DD HHmmss\<eredeti relatív útvonal>` (futásonként egy időbélyeg-mappa, helyi idő). Ugyanaz, mint az FFS „Replace” stílusa — az átállók ismerik.
- Megőrzés: `keep_days` (alap 21) → a futás végén a régebbi időbélyeg-mappák törlése (csak a mappanév alapján, csak a saját Versions mappában, és csak ha a név a minta szerinti). `max_versions_per_file` (opcionális): fájlonként a legutóbbi N változat marad.
- A megőrzési törlés akkor is lefut, ha a futás `warning`; `failed` futásnál **nem** fut (ne fogyjanak a verziók egy hibás időszakban).

### 6.5 Törlési biztosíték (`delete_guard_pct`, `delete_guard_min_files`)
A tervezési fázis után, **mielőtt bármit törölnénk / Versions-be mozgatnánk**: ha a törlendő (mirror-ban eltűnt) fájlok száma ≥ `delete_guard_min_files` ÉS ≥ a célon lévő fájlok `delete_guard_pct` %-a → a futás **nem töröl semmit**, a másolásokat elvégzi, eredmény `failed` „törlési biztosíték: N fájl (P%) törlődne”, CRIT e-mail. Feloldás: GUI-ban „Egyszeri jóváhagyás” (a következő futás engedélyezett) vagy `bf run <jobId> --allow-mass-delete`. Ugyanígy: **üres vagy nem elérhető forrás esetén a futás `failed`**, sosem tükrözzük az ürességet.

### 6.6 VSS
- `vss`: `never` | `when_available` (alap) | `required`.
- Futásonként kötetenként egy snapshot (a források kötetei), a forrásutak átfordítása a snapshot-eszközútra. A futás végén (hibánál is) a snapshot törlése. Induláskor a szolgáltatás a saját, árván maradt snapshotjait törli (azonosítás: a snapshot-készlet saját context/attribútuma vagy az `history.db`-ben rögzített snapshot-ID).
- `when_available`: ha a snapshot nem készül el, a futás VSS nélkül megy és `warning`.
- Writerek: v1-ben `VSS_BT_COPY` (copy-only), a writerek bevonásával — így a futás nem töri meg más mentőprogramok láncát.

### 6.7 Kizárások
Glob minták (`*`, `?`, `**`), mappára `\név\` formában; alapértelmezett kizárások (szerkeszthető lista): `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`, `\System Volume Information\`, `\$RECYCLE.BIN\`, `*.bfpart`, `~$*`, `Thumbs.db`. A VSS „FilesNotToSnapshot” regisztrációs listáját nem írjuk felül.

### 6.8 Tervezés és dry-run
Futás két fázisban: (1) bejárás és terv (másolandó, verziózandó, törlendő listák, méretek) → (2) végrehajtás. `bf run <jobId> --dry-run` csak a tervet írja ki (és a futásnaplóba). A bejárás az első futás után gyorsítható (v2: helyi index); v1-ben teljes bejárás.

## 7. restic-motor (`type: restic`)

### 7.1 Bináris
- `restic.exe` a programmappában, rögzített verzióval (`third_party/restic/VERSION`: verzió, letöltési URL, SHA-256). A szolgáltatás induláskor ellenőrzi a hash-t; eltérésnél a restic-feladatok `failed` „restic.exe sérült vagy módosított”.
- A végrehajtó a build idején aktuális legfrissebb stabil restic-verziót rögzítse, és a felhasznált kapcsolókat / kilépési kódokat **annak a verziónak a dokumentációjából** ellenőrizze.

### 7.2 Hívás
- Mindig `--json` kimenettel, a parancssorban titok **nincs**: a jelszó `RESTIC_PASSWORD` környezeti változóban megy a gyerekfolyamatnak (vagy `--password-command` egy saját segédre — a végrehajtó választ, a lényeg: ne legyen a parancssorban és ne legyen fájlban titkosítatlanul). A célhoz szükséges egyéb titkok (S3-kulcs, SFTP) szintén környezeti változóként (`env_refs`).
- `--cache-dir C:\ProgramData\BackupForge\restic-cache`, `--no-scan` nem kell (a haladás miatt jó a scan), `--host <instance/hostname>` rögzített (a gép átnevezése ne törje a forget-csoportosítást).
- `backup`: `restic backup <források> --json --tag bf:<jobId> [--tag …] [--use-fs-snapshot ha vss != never] [--exclude-file …]`. A JSON-ból: haladás (`status` üzenetek → GUI-nak IPC-n), a végén `summary` (új/módosult/változatlan fájlok, hozzáadott bájtok, snapshot ID).
- Kilépési kódok (a rögzített verzió dokumentációja szerint ellenőrizendő): `0` → `ok`; `3` (a snapshot elkészült, de egyes fájlok nem voltak olvashatók) → `warning`, a hibás fájlok a futásnaplóba; minden más → `failed` a stderr utolsó soraival. Ismert külön esetek: nem létező tároló, rossz jelszó, nem sikerült zárolni → érthető magyar üzenet.
- **Tároló inicializálása**: csak a GUI „Új tároló” varázslójából / `bf repo init <repoId>`, kifejezett megerősítéssel — mentéskor soha nem inicializálunk automatikusan (egy elgépelt útvonal ne hozzon létre csendben új, üres tárolót).
- **Zárak**: ha a backup „locked” hibával áll meg, és a zár a mi `--host`-unké, 30 percnél régebbi, és nálunk nem fut feladat ezen a tárolón → `restic unlock` (csak elavult zárak), egyszeri újrapróbálás. Egyébként `failed`.
- **Karbantartás** (tárolónként, saját ütemezéssel, a tárolózárral):
  - `forget --prune` a `retention` szabályaival, `--group-by host,tags` — a feladatonkénti `bf:<jobId>` tag miatt feladatonként külön csoportosul. Ha `append_only_server: true`, a forget/prune nem fut a kliensről (az append-only szerveren a szerver oldali karbantartás feladata), a státusz ezt jelzi.
  - `check` hetente, `--read-data-subset` (alap 5%) — hogy a tároló olvashatósága is ellenőrizve legyen. Hibánál CRIT.
- **Visszaállítás**: `snapshots --json`, `ls <snapshot> --json` (böngészéshez), `restore <snapshot>:<útvonal> --target <hely> [--include …]`. Alapból új mappába (`<cél>\Restore_<dátum>`), eredeti helyre csak kifejezett megerősítéssel és `--overwrite`-politika megadásával (ha a verzió támogatja).

### 7.3 Jelszó
A tároló jelszavát a telepítéskor / tároló-létrehozáskor a GUI generálja (32 karakter) és megmutatja **egyszer**, „Mentsd el biztonságos helyre (jelszókezelő) — nélküle a mentés nem állítható vissza” figyelmeztetéssel, és egy nyomtatható „helyreállítási lapot” kínál (tároló helye, jelszó, dátum, gépnév). A jelszó a `secrets.dat`-ba kerül.

## 8. SQL Server-motor (`type: sql`)

### 8.1 Felderítés
- Példányok: a szolgáltatásokból (`MSSQLSERVER`, `MSSQL$<név>`) és a registryből (`HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL`). A GUI listát ad, a felhasználó választ.
- Adatbázisok: `sys.databases` (állapot `ONLINE`, nem snapshot, nem `tempdb`), recovery model, méret.
- `databases.mode`: `all_user` (minden felhasználói, **az új adatbázisok automatikusan bekerülnek**) | `selected` (`include` lista). `include_system`: master, model, msdb mentése (alap: igen).

### 8.2 Mentés (adatbázisonként, sorban)
1. `BACKUP DATABASE [db] TO DISK = N'<staging>\<jobId>\<db>_<yyyyMMdd_HHmmss>.bak' WITH CHECKSUM, INIT, FORMAT [, COPY_ONLY] [, COMPRESSION | NO_COMPRESSION], STATS = 5` — a haladás az `InfoMessage`-ekből (GUI-nak IPC-n).
2. Ha `verify`: `RESTORE VERIFYONLY FROM DISK = … WITH CHECKSUM`.
3. Az adatbázis eredménye: `ok` (mindkettő sikeres) | `failed` (hibaszöveg: SQL hibakód + üzenet).
- `compression`: `auto` → ha a lánc következő tagja restic, **tömörítés nélkül** (deduplikáció miatt); különben tömörítéssel, ha az edíció támogatja (Express nem → tömörítés nélkül, INFO). `on` / `off` kézzel.
- A futás eredménye: minden db `ok` → `ok`; bármelyik felhasználói db `failed` → `failed`; csak rendszer-db hibás → `warning`.
- Egy db hibája nem állítja meg a többit.

### 8.3 Átmeneti mappa (staging)
- `C:\ProgramData\BackupForge\staging\<jobId>\` (konfigurálható, pl. másik kötetre: `"staging_path"`). A `BACKUP`-ot az **SQL Server szolgáltatásfiókja** írja, ezért a telepítő / a GUI „Jogosultságok beállítása” lépése a staging mappára írási jogot ad a példány szolgáltatásfiókjának (`NT SERVICE\MSSQLSERVER` / `NT SERVICE\MSSQL$<név>`, vagy ha egyedi fiókkal fut, annak). A mappa többi részéhez az SQL-fiók nem fér hozzá.
- Szabad hely előellenőrzés: a db-k becsült mentési mérete (a használt adatlapok alapján) + 10% < szabad hely; különben a futás `failed` „nincs elég hely az átmeneti mappában” — **mielőtt** a BACKUP elindulna.
- `keep_local`: adatbázisonként az utolsó N sikeres `.bak` marad a staging-ben (gyors visszaállításhoz); a régebbieket a **sikeres láncvég** után töröljük (ha a láncolt restic / fájlmentés sikertelen, nem törlünk).
- `@staging:<jobId>` forrás: a láncolt feladat ennek a mappának az **aktuális tartalmát** menti (fájlmentésnél tükörként a Versions-sel, restic-nél snapshotként).

### 8.4 Recovery model figyelmeztetés
Ha egy mentett adatbázis `FULL` vagy `BULK_LOGGED` recovery modelben van, és nincs utána naplómentés (`msdb.dbo.backupset` `type='L'` az utolsó 24 órában), a futás `warning` „<db>: FULL recovery model, de nincs naplómentés — a naplófájl korlátlanul nő. Állítsd SIMPLE-re, vagy kapcsold be a naplómentést (v2).” — feladatonként némítható (`"ignore_full_recovery": ["db"]`).

### 8.5 Egyéb ellenőrzések
- `checkdb` (opcionális ütemezés): `DBCC CHECKDB([db]) WITH NO_INFOMSGS, ALL_ERRORMSGS` a felhasználói db-kre; hiba → CRIT, az üzenetek a futásnaplóba. Külön futásként jelenik meg az előzményben (`kind: checkdb`).
- msdb-ellenőrzés: minden futás után lekérdezzük a db-k utolsó teljes mentését az `msdb.dbo.backupset`-ből (bárki készítette) — a státuszba kerül (`last_full_any`), a BackupPulse látja, ha egy db-ről senki nem ment.

### 8.6 Hitelesítés és jogosultság
- `auth: windows` (alap): a szolgáltatás `NT SERVICE\BackupForge` service SID-ként kapcsolódik (a LocalSystem `NT AUTHORITY\SYSTEM` loginját nem tesszük sysadminná).
- `auth: sql`: SQL-login, jelszó a `secrets.dat`-ban.
- **„Jogosultságok beállítása”** (GUI-gomb, `bf sql grant <jobId>`): egy sysadmin hitelesítést kér (Windows: az aktuális admin user; vagy SQL-login) — **nem tárolja** —, és lefuttatja a szükséges T-SQL-t: login létrehozása a `NT SERVICE\BackupForge`-nak, a mentéshez szükséges adatbázis-szerepkör (`db_backupoperator`) minden érintett db-ben (rendszer-db-kben is), a `RESTORE VERIFYONLY`-hoz és a felderítéshez szükséges szerver-szintű jog, `msdb` olvasás a `backupset`-hez, `checkdb` esetén a szükséges jog. **A végrehajtó a pontos minimális jogkészletet a Microsoft-dokumentációból állapítsa meg és dokumentálja** (`docs/SQL-PERMISSIONS.md`); a GUI a futtatandó T-SQL-t futtatás előtt megmutatja.
- `all_user` módban **új adatbázis**, amire nincs jog: a db `failed` „nincs jogosultság (új adatbázis?) — futtasd a Jogosultságok beállítását”. A felderítés ezt előre jelzi a GUI-ban.

## 9. Előzmények, naplók, státusz

### 9.1 `history.db` (SQLite)
Táblák (a végrehajtó finomíthatja, eltérés → CLAUDE.md):
- `runs` (id, job_id, chain_run_id, kind [backup|forget_prune|check|checkdb|retention], trigger [schedule|catch_up|manual|chain], started_at, ended_at, result, summary, files_*, bytes_*, error_count, warning_count, details_json)
- `run_items` (run_id, level, path, message) — az első 50 hiba/figyelmeztetés futásonként
- `sql_db_results` (run_id, instance, db, system, result, backup_file, size_bytes, duration_s, verify_ok, error)
- `config_changes` (id, at, by [gui-user|cli|manual-edit], summary, diff_json) — ki, mikor, mit kapcsolt ki / módosított
- `vss_snapshots` (id, created_at, run_id, snapshot_id, deleted_at)
- `notifications` (id, at, kind, job_id, result, status, error)
Megőrzés: `history_retention_days` (alap 400).

### 9.2 Naplók
- Szolgáltatásnapló: `logs\service-YYYYMMDD.log`, 30 nap.
- Futásnapló: `logs\jobs\<jobId>\<runId>.log` — a teljes részletes napló (kihagyott fájlok, restic stderr, SQL InfoMessage-ek). A megőrzés a `log_retention_days` szerint.
- Eseménynapló (`Application`, forrás `BackupForge`): induláskor / leálláskor INFO, feladat `warning` → Warning (eseményazonosító 2001), `failed` → Error (2002), konfighiba → Error (2010), törlési biztosíték → Error (2003). Az eseményazonosítók listája a `docs/EVENT-IDS.md`-be (RMM-ekhez).

### 9.3 `status\status.json`
A BackupPulse-nak és bármely külső figyelőnek. **Szerződés: `STATUS-CONTRACT.md`** (verziózott séma). A szolgáltatás percenként és minden futás-állapotváltáskor atomikusan írja (temp fájl + `File.Replace`). A `heartbeat` mezőből látszik, hogy a szolgáltatás él-e.

### 9.4 Konfigváltozások naplózása
Minden mentett konfigváltozás: `config_changes` sor + INFO a szolgáltatásnaplóba. Ha egy feladatot **letiltanak**, a `status.json`-ban `enabled: false` + `disabled_since`, a heti riportban külön figyelmeztető szekció: „Letiltott feladatok: … (2026-07-13 óta)”. Ez az eset korábban egy kikommentezett sor miatt hónapokig észrevétlen maradt — a cél, hogy ez ne fordulhasson elő.

## 10. Értesítések

### 10.1 E-mail
- Azonnali: `failed` → hibalevél; `warning` → figyelmeztetés (kikapcsolható); helyreállás (`failed`/`warning` után az első `ok`) → helyreállás-levél (kikapcsolható). Tárgy: `[BackupForge][<instance_name>] HIBA: <feladat> — <summary>`. Törzs: HTML + szöveges alternatíva: feladat, eredmény, időtartam, összesítő, az első 20 hiba, a futásnapló helye a gépen. Csatolmány: a futásnapló, ha < 1 MB (különben az utolsó 200 sora).
- Ismétlődő hiba: ugyanarra a feladatra 24 órán belül legfeljebb 1 hibalevél, ha a hiba szövege azonos (a többit a heti riport összesíti); eltérő hiba → új levél.
- Teszt-gomb a GUI-ban, `bf notify test`.
- SMTP-hiba: 3 újrapróbálás (1, 5, 15 perc), utána eseménynapló Error (2020) és a státuszban `notifications.last_error`.

### 10.2 Belső watchdog (az appon belül)
Percenként: minden engedélyezett feladatnál, ha `now − last_ok > expected_interval_h × 1.5 + 1 óra` (és nem fut) → `stale` állapot a státuszban + egyszeri e-mail „<feladat>: X órája nincs sikeres mentés” (24 óránként ismételve). Ez akkor is jelez, ha a futások „lefutnak”, de nem sikeresek, vagy ha az ütemezés elromlott. **A gép / szolgáltatás leállását az app magáról nem tudja jelezni** — erre a BackupPulse offline-figyelése vagy a `heartbeat_url` való.

### 10.3 Heti riport (`weekly_report`)
HTML e-mail: összesítő állapot (OK / FIGYELEM / HIBA), feladatonként: futások száma, sikeres / figyelmeztetés / hibás, utolsó sikeres, átvitt adat; letiltott feladatok; célok szabad helye (+ `min_free_gb` alatti figyelmeztetés); Versions mappák mérete; restic-tárolók: snapshotok száma, utolsó `check` eredménye, utolsó `prune`; SQL: db-nkénti utolsó sikeres mentés, recovery model figyelmeztetések, utolsó CHECKDB; konfigváltozások a héten. (A korábbi `ffs-weekly-report.ps1` mintájára.)

### 10.4 `heartbeat_url` (opcionális)
Ha be van állítva: minden sikeres futás végén és óránként HTTP GET/POST (healthchecks.io-kompatibilis: `<url>` siker, `<url>/fail` hiba). Titok nincs benne a naplóban.

## 11. Konfigurátor (WPF GUI) és CLI

### 11.1 IPC
- Named pipe: `\\.\pipe\BackupForge`, ACL: SYSTEM + Administrators (a GUI emelt jogosultsággal fut — `requireAdministrator` manifest). Üzenetek: JSON, kérés/válasz + szerver-push (haladás, állapotváltozás).
- Műveletek: `status.get`, `config.get`, `config.validate`, `config.save` (+ titkok külön `secrets.set` — a titok soha nem jön vissza olvasáskor, csak `is_set: true`), `job.run`, `job.cancel`, `job.dryrun`, `history.list`, `history.get`, `log.tail`, `restore.*`, `sql.discover`, `sql.grant`, `repo.init`, `repo.snapshots`, `repo.ls`, `notify.test`, `target.test`.
- Verzióegyeztetés: a kliens és a szolgáltatás protokollverziót cserél; eltérésnél érthető hiba.

### 11.2 GUI oldalak
1. **Áttekintés**: feladatok kártyákon (állapot, utolsó futás, következő futás, „Futtatás most”, „Megszakítás”, haladás), célok szabad helye, figyelmeztető sáv (letiltott feladat, stale, konfighiba).
2. **Feladatok**: lista + szerkesztő varázsló típusonként (forrás tallózás, kizárások, cél, ütemezés emberi nyelven is kiírva: „4 óránként 06:15-től”), „Próbafuttatás” (dry-run eredmény táblázatban).
3. **Célok és tárolók**: SMB-cél (útvonal, felhasználó, jelszó, „Kapcsolat tesztelése” — a szolgáltatás nevében, LocalSystem-ként!), helyi cél, restic-tároló (új / meglévő, jelszó-varázsló, helyreállítási lap).
4. **SQL**: példányok, adatbázisok (recovery model, méret, utolsó mentés bárkitől), „Jogosultságok beállítása”.
5. **Előzmények**: szűrhető lista, futás részletei, futásnapló megnyitása.
6. **Visszaállítás**: forrás kiválasztása (fájlmentés: Versions időpontok + a jelenlegi tükör; restic: snapshotok) → fa-böngésző → cél (alapból új mappa) → indítás.
7. **Értesítések**: SMTP, címzettek, teszt; heti riport; heartbeat URL.
8. **Beállítások**: párhuzamosság, megőrzések, nyelv, konfig export/import (titkok nélkül; importnál a hiányzó titkokat bekéri).

### 11.3 CLI (`bf.exe`)
`bf status [--json]`, `bf jobs`, `bf run <jobId> [--dry-run] [--allow-mass-delete] [--wait]`, `bf cancel <jobId>`, `bf history [<jobId>] [--last N]`, `bf log <runId>`, `bf config validate [<fájl>]`, `bf config reload`, `bf config export|import`, `bf secret set <ref>` (jelszót stdin-ről vagy rejtett bekéréssel), `bf repo init|snapshots|check <repoId>`, `bf restore …`, `bf sql discover|grant`, `bf notify test`. Kilépési kód: 0 siker, 1 hiba, 2 használati hiba, 3 a szolgáltatás nem elérhető. A `--wait` a futás végéig vár, és a futás eredményét adja kilépési kódként (0 ok, 10 warning, 11 failed) — szkriptekhez.

## 12. Telepítő (Inno Setup)

- Egy `BackupForge-Setup-<verzió>.exe`; adminjog kell.
- Lépések (interaktív): nyelv → licenc (az app licence + restic BSD-2) → telepítési hely → „Konfigurátor indítása a végén”. A konfigurálás a GUI-ban történik (a telepítő nem kér be feladatokat).
- **Csendes telepítés**: `setup.exe /VERYSILENT /SUPPRESSMSGBOXES /CONFIG="ugyfel.json" [/SECRETS="ugyfel.secrets.json"]` — a konfig validálva kerül a helyére; a secrets-fájl (titkok sima JSON-ban) beolvasás után a `secrets.dat`-ba kerül és **a forrásfájlt a telepítő nem törli, de figyelmeztet** (a naplóba), hogy törölni kell.
- Telepítéskor: szolgáltatás regisztrálása (recovery-beállításokkal, service SID `unrestricted`), ProgramData ACL-ek, eseménynapló-forrás, Start menü parancsikon (GUI), `bf.exe` a PATH-ba (opcionális).
- **Frissítés**: ugyanaz a telepítő, a meglévő konfig és előzmények megmaradnak; ha a konfigséma változott, a szolgáltatás első induláskor migrál (és `config.json.bak.premigrate`-t hagy).
- **Eltávolítás**: szolgáltatás leállítása és törlése, programmappa, eseménynapló-forrás, PATH-bejegyzés. Kérdés: „Megtartod a beállításokat és az előzményeket?” (alap: igen; csendes eltávolításnál `/PURGE` kell a törléshez). **A mentési célokhoz, a Versions mappákhoz és a restic-tárolókhoz az eltávolító soha nem nyúl.** Az árva VSS-snapshotokat törli.
- Kódaláírás: v1-ben opcionális (nyitott kérdés, 18. pont); ha van tanúsítvány, a CI aláírja az exe-ket és a telepítőt.

## 13. Visszaállítás (biztonsági szabályok)
- Alapértelmezett cél: új mappa (`<választott hely>\Restore_<YYYY-MM-DD HHmmss>`); eredeti helyre csak külön megerősítéssel („A meglévő fájlok felülíródnak — a felülírt fájlok a `…_RestoreBackup_<időbélyeg>` mappába kerülnek”).
- Visszaállítás közben az érintett feladat nem fut (célzár).
- A visszaállítás is futásként kerül az előzménybe (`kind: restore`), eredménnyel és naplóval.

## 14. Biztonság
- **Titkok**: `secrets.dat` — DPAPI `LocalMachine` + alkalmazásspecifikus entrópia, ACL: csak SYSTEM. A GUI/CLI titkot csak írni tud (IPC-n), olvasni nem. Naplóba, státuszba, e-mailbe, kivételszövegbe titok nem kerülhet (a naplózó egy titok-maszkoló szűrőn megy át, teszttel).
- Konfig-export: titkok nélkül.
- Named pipe: csak SYSTEM + Administrators. A szolgáltatás minden IPC-kérést validál (a GUI nem megbízható bemenet).
- Hálózati cél: a hitelesítés `WNetAddConnection2`-vel, meghajtóbetű nélkül, a futás végén `WNetCancelConnection2`. Ha a szerverhez már van más hitelesítésű kapcsolat (1219-es hiba), érthető üzenet.
- Zsarolóvírus: a fájlmentés célja írható a gépről — ez a modell korlátja. A dokumentáció javasolja a restic + `rest-server --append-only` (vagy változtathatatlan objektumtár) kombinációt a második másolatnak; a GUI a tároló-varázslóban ezt meg is említi.
- A `restic.exe` hash-ellenőrzése induláskor (7.1).
- A telepítő és az exe-k aláírása, ha van tanúsítvány (12. pont).

## 15. Tesztelés
- **Unit (Linuxon is fut)**: ütemező (interval / daily / weekly / monthly, DST-váltás mindkét iránya, catch-up), feladatsor + zárak + láncok (párhuzamosság, kihagyás, lánc-megszakadás), konfig-validálás (minden hibaág), összehasonlítás (2 mp, ±1 óra, kis-/nagybetű), tervezés (mirror / update, Versions, törlési biztosíték), Versions megőrzés (mappanév-minta, idegen mappát nem töröl), restic JSON-feldolgozás (rögzített kimenetek a `fixtures/`-ben: backup summary, status, exit 3, rossz jelszó, lock), státusz-JSON építése (a `schema/status.schema.json`-nak megfelel), titok-maszkolás, e-mail-összevonás.
- **Integráció (Windows-runner, GitHub Actions `windows-latest`)**: fájlmentés valós NTFS-en (hosszú útvonal, readonly, zárolt fájl VSS-sel), restic valós bináris egy helyi tárolóval (init, backup, exit 3 egy zárolt fájllal VSS nélkül, forget/prune, restore), SQL — ha a runneren telepíthető SQL Server Express / LocalDB, akkor backup + verify; különben a végrehajtó jelzi, és kézi tesztlistát ír.
- **Kézi tesztlista** (`docs/MANUAL-TESTS.md`): telepítés / frissítés / eltávolítás, szolgáltatás-újraindítás futás közben, hálózati cél megszakadása, SMB-hitelesítés, GUI-folyamatok, e-mailek kinézete Outlookban.
- Minden fázis végén: `dotnet test` zöld (Linuxon a platformfüggetlen rész, CI-ban minden).

## 16. Elfogadási feltételek (v1 kész)
1. Egy tiszta Windows Server 2019/2022 gépen a telepítő után a GUI-ból beállítható: fájlmentés NAS-ra Versions-szel, SQL Express mentése láncban restic-tárolóba, e-mail-értesítés — és ezek ütemezetten, felügyelet nélkül futnak.
2. A szolgáltatás újraindítása / a gép újraindítása után az elmaradt futások pótlódnak, a félbemaradt futás `interrupted`, nem marad `.bfpart`, árva VSS-snapshot vagy restic-zár.
3. Üres / elérhetetlen forrás, illetve tömeges törlés esetén a fájlmentés nem töröl a célon, és CRIT e-mailt küld.
4. Egy letiltott feladat a heti riportban és a `status.json`-ban látszik.
5. A `status.json` megfelel a `STATUS-CONTRACT.md`-nek (séma-validálással tesztelve).
6. A restic-tárolóból és a Versions mappából a GUI-val vissza lehet állítani egy fájlt és egy mappát.
7. Titok nem jelenik meg naplóban, státuszban, e-mailben, konfig-exportban (teszttel ellenőrizve).
8. Az eltávolítás a mentési célokhoz nem nyúl; `/PURGE` nélkül a beállítások megmaradnak.

## 17. Fázisok (mindegyik végén: tesztek zöldek, CLAUDE.md frissítve, commit + push)

| Fázis | Tartalom | Kész, ha |
|---|---|---|
| **F0** | Solution-szerkezet (3. pont), CI (build + unit tesztek Linuxon és Windows-runneren), `Core`: konfigmodell + validálás + `config.schema.json`, naplózás, óra/fájlrendszer-absztrakció | `dotnet build` + `dotnet test` zöld a CI-ban; validátor-tesztek minden szabályra |
| **F1** | Szolgáltatás-host, ütemező, feladatsor + zárak + láncok (motorok helyett teszt-motorral), `history.db`, CLI alap (`status`, `jobs`, `run`, `cancel`, `history`, `config validate/reload`), IPC alap | a szolgáltatás Windowson fut, egy teszt-feladatot ütemezetten és kézzel futtat, a CLI látja; ütemező/sor unit tesztjei (DST is) zöldek |
| **F2** | Fájlmentés-motor (6. pont teljesen: módok, összehasonlítás, másolás, Versions + megőrzés, törlési biztosíték, VSS, hosszú utak, kizárások, dry-run), hálózati célok (WNet + DPAPI-titkok, `secrets.dat`), e-mail (azonnali + helyreállás), eseménynapló, belső watchdog | az elfogadási feltételek 2–3. pontja fájlmentésre teljesül; integrációs tesztek Windows-runneren |
| **F3** | `status.json` (STATUS-CONTRACT), heti riport, konfigváltozás-napló, `heartbeat_url` | a séma-validált státusz minden feladattípusra (a még nem létezőkre üres blokkal), heti riport e-mail mintája a `docs/`-ban (képernyőkép vagy HTML) |
| **F4** | restic-motor (7. pont): bináris + hash, init, backup, kilépési kódok, zárak, forget/prune, check, snapshots/ls/restore a CLI-ből | integrációs teszt valós restic-kel; fixture-alapú unit tesztek |
| **F5** | SQL-motor (8. pont): felderítés, backup + verify, staging + keep_local, láncvég-takarítás, recovery-model figyelmeztetés, CHECKDB, msdb-ellenőrzés, jogosultság-beállítás + `docs/SQL-PERMISSIONS.md` | SQL Express-en (CI vagy kézi lista) végig: SQL → restic lánc |
| **F6** | WPF GUI (11.2 mind) + visszaállítás (13. pont) | a 16. pont 1. és 6. eleme GUI-ból végigvihető; kézi tesztlista |
| **F7** | Telepítő (12. pont): interaktív + csendes + frissítés + eltávolítás, CI-release (`v*` tag → telepítő a Release-be), `docs/EVENT-IDS.md`, `docs/MANUAL-TESTS.md` | 16. pont teljesül; kézi tesztlista lefutott egy tiszta VM-en |
| **F8** | BackupPulse-integráció: a BackupPulse repóban CR (a `collect.ps1` olvassa a `status.json`-t + szerveroldali szabályok) — **külön repó, külön CR-dokumentum, a chat írja** | pilot gépen a BackupPulse dashboardon megjelennek a BackupForge-feladatok |

## 18. Nyitott kérdések (alapértelmezéssel — ezekkel lehet haladni)
1. **Végleges név** — munkanév: BackupForge. (Átnevezés: namespace + telepítő + dokumentáció; a F7 előtt kell eldönteni.)
2. **Licenc** — alapértelmezés: az app **MIT** (a restic BSD-2 mellé illik; részletek `BUSINESS.md`). A repó addig privát.
3. **Kódaláírás** — tanúsítvány nincs; v1 aláíratlan (SmartScreen-figyelmeztetés lehet). Döntés a pilot előtt.
4. **.NET 10 és Windows Server 2016** — ha a runtime nem támogatja, a Server 2016 kiesik (jelzendő).
5. **GUI-keretrendszer** — WPF (alap). Ha a végrehajtó a WinUI 3-at vagy Avalonia-t jobbnak látja, előbb kérdezzen.
6. **Ügyfél oldali „csak olvasó” felület** (ügyfél megnézheti, ment-e) — v2.
7. **Átállás FFS/Cobian-ról** — importáló (`.ffs_batch` → files-feladat, Cobian `MainList.lst` → feladatok) v1.1-ben; a BackupPulse már ismeri mindkét formátumot (lásd ott a CLAUDE.md-t).

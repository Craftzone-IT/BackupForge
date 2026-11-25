# CR-001 — Javítások a v1 átnézése alapján

**Állapot: KÉSZ (2026-11-25)** — részletek: CLAUDE.md „CR-001 — KÉSZ” és „Eltérések a CR-001-től”.
Ág: `claude/blissful-goldberg-vlt55t` (ugyanaz, mint az F0–F7). A `main`-be olvasztás ezután jön.

A F0–F7 után célzott átnézés készült a törlő kódutakra (mirror-törlés, Versions-megőrzés, staging-takarítás, `.bfpart`, törlési biztosíték, eltávolító) és két CI-kérdésre. A törlési logika egy feladaton belül rendben van: a nem listázható forrás alatt nincs törlés, a reparse pointokat nem követi, a kizárt fájlokat a célon sem törli, a megőrzés csak a minta szerinti mappákat bántja, a staging-takarítás csak a saját névmintájú `.bak`-okat. **A hiba a feladatok közötti átfedésnél van**, és van egy csendes VSS-leromlás.

Sorrend: 1 → 2 → 3, utána a nitek. Mindegyikhez teszt kell; a fázis akkor kész, ha a CI zöld, és a `CLAUDE.md` frissítve van (új szakasz: „CR-001 — KÉSZ”, eltérések).

---

## 1. (FONTOS) Feladatok közötti útvonal-átfedés: egy tükör más feladat adatait törölheti

### Hiba
A `ConfigValidator.ValidateFilesJob` a feladatok között csak a **pontos egyezést** nézi (`target.shared`: két tükör ugyanazon az úton). A forrás–tükör átfedést csak a feladaton **belül** vizsgálja. Nincs ellenőrzés arra, hogy egy `mirror` módú fájlfeladat tükörmappája **tartalmazza** vagy **benne van**:
- egy másik fájlfeladat tükrében vagy Versions mappájában,
- egy másik feladat (files / restic) forrásában,
- egy helyi restic-tárolóban (`restic_repos[].location`, ha helyi vagy UNC út),
- egy SQL-feladat staging mappájában (`staging_path` vagy az alapértelmezett `ProgramData\BackupForge\staging`),
- a BackupForge adatmappájában (`ProgramData\BackupForge`).

### Hibaforgatókönyv (valószerű beállítás)
- `nas1` cél: `\\nas\backup\srv01`.
- „A” feladat: `D:\Data` → `nas1`, **subdir üres** (a célgyökérbe ment), mirror, a Versions abszolút úton máshol.
- „B” feladat: `D:\Company` → `nas1`, subdir `Company` (tükör: `\\nas\backup\srv01\Company`, Versions: `\\nas\backup\srv01\Company_Versions`).
- Esetleg egy restic-tároló is: `\\nas\backup\srv01\restic`.

Az „A” minden futáskor mindent „fölöslegesnek” lát a célgyökérben, ami nincs a `D:\Data`-ban: a `Company`, a `Company_Versions` és a `restic` mappa tartalmát az „A” Versions mappájába mozgatja (verziózás nélkül **törli**), majd `keep_days` után végleg törli. **A restic-tároló így tönkremegy, a „B” mentése és verziói eltűnnek.** A törlési biztosíték csak akkor fog, ha ez a cél fájljainak legalább 20%-a és legalább 50 fájl — egy nagy „A” mellett (pl. 100 000 fájl) egy kisebb „B” vagy tároló (pl. 5 000 fájl, 5%) **nem** váltja ki. Az erőforrászár sem véd: a zár `target_id + subdir`, a két feladatnak más a zárja, így az „A” `CleanupParts`-ja a párhuzamosan futó „B” félkész `.bfpart` fájljait is törölheti.

Ugyanez helyi célnál: ha az „A” tükre `X:\`, és egy másik feladat forrása `X:\Archiv`, az „A” a másik feladat **forrását** mozgatja / törli.

### Javítás
**1a. Validálás** (`ConfigValidator`, minden feladat feldolgozása után, egy menetben): gyűjtsd össze a „védett utakat” feladat-azonosítóval:
- minden fájlfeladat tükre és Versions mappája;
- minden feladat forrása (a `@staging:` a feloldott staging-útra);
- minden helyi / UNC restic-tároló helye (a `sftp:`, `s3:`, `rest:` stb. kihagyva);
- minden SQL-feladat staging mappája (`StagingPaths.For`, az alapértelmezett gyökérrel);
- a `DataPaths.Root`.

Hiba (`path.overlap_job`, mezőszintű, hu/en szöveggel: „A(z) <út> átfedi a(z) <másik feladat> <szerep> útvonalát: <út>”), ha egy **mirror módú** fájlfeladat tükre egy másik bejegyzés útjával azonos, azt tartalmazza, vagy benne van (`PathUtil.Overlaps`). Update módú feladatnál ugyanez **figyelmeztetés** (nem töröl, de összekeveredhet). Két Versions mappa átfedése is hiba (a megőrzés a másikét törölheti).

**1b. Futásidejű védőháló** (a validátor mellett, mert a konfig kézzel is szerkeszthető, és a hibás konfignál a szolgáltatás az utolsó érvényeset használja — de egy régi `config.json` is lehet hibás): a `FileJobEngine` a terv után, a törlési biztosíték előtt távolítson el a tervből minden `DeleteFile` / `DeleteDir` tételt, amelynek abszolút útja egy másik feladat védett útja **alatt vagy azon** van, és ezt `warning`-ként jelezze („N tétel nem törlődött, mert a(z) <feladat> mappájához tartozik”). A `CleanupParts` se lépjen be ezekbe a mappákba. Ehhez a `JobRunContext` kapja meg a védett utak listáját (a `ServiceRuntime` állítja össze ugyanazzal a függvénnyel, amit a validátor használ — egy kódút).

**1c. Erőforrászár**: a célzár az útvonal-átfedést is vegye figyelembe (ha két feladat útja átfedi egymást, ne fusson párhuzamosan) — a validátor után ez elvileg nem fordulhat elő, de olcsó és a régi / kézi konfig ellen véd.

### Tesztek
- Validátor: a fenti forgatókönyvek mindegyike (gyökér-tükör + almappás tükör; tükör + más Versions; tükör + más feladat forrása; tükör + helyi restic-tároló; tükör + SQL staging; tükör + `DataPaths.Root`; két Versions átfedése; update mód → figyelmeztetés, nem hiba; nem átfedő, de közös előtagú nevek — `\\nas\backup\srv01\Comp` vs. `…\Company` — **nem** hiba).
- Motor (`FaultyFileSystem` / memóriabeli FS): gyökér-tükör, mellette egy idegen feladat tükre, Versions-e és egy „restic” mappa több ezer fájllal, a törlési biztosíték küszöbe alatt → futás után az idegen mappák bájtra érintetlenek, az eredmény `warning`.
- `CleanupParts`: az idegen feladat mappájában lévő `.bfpart` megmarad.

---

## 2. (FONTOS) A writer nélküli VSS-tartalék csendes

### Hiba
`BackupForge.Windows/Vss.cs`: ha a `GatherWriterMetadata` 90 mp alatt nem fejeződik be, a kód writerek nélkül (crash-konzisztens) készít pillanatképet, és ezt egy **statikus** `WritersSkipped` eseménnyel jelzi — erre az eseményre **semmi nem iratkozik fel**. Ilyenkor a futás `ok`, a futásnaplóban, a `status.json`-ban és az e-mailben nincs nyoma. Ez pont az a csendes leromlás, amit az app el akar kerülni (SPEC 6.6: ha a VSS nem a kért módon készül, `warning`).

### Javítás
- Szüntesd meg a statikus eseményt (párhuzamos futásoknál amúgy is rossz feladathoz kötné). Az `ISnapshotSession` kapjon egy `IReadOnlyList<string> Warnings` (vagy `bool WritersSkipped` + `string? Reason`) tulajdonságot; a `Vss.Create` ebbe írja a tartalék okát (a nem válaszoló writer neve, ha kideríthető; különben a lépés és az időkorlát).
- `FileJobEngine`: ha a session jelez, `outcome.AddWarning(null, Texts.Format(culture, "files.vss_no_writers", reason))` + a futásnaplóba Warning. Szöveg (hu): „VSS: a writerek nem válaszoltak (<ok>), a pillanatkép alkalmazás-konzisztencia nélkül készült.” (en megfelelő). A futás eredménye így `warning`.
- A heti riport és a `status.json` a meglévő módon mutatja (a futás `warning`, az `errors[]`-ben a szöveg).

### Tesztek
- Unit: egy hamis `ISnapshotProvider`, ami `WritersSkipped`-del tér vissza → a futás `warning`, a szöveg az outcome-ban és a naplóban.
- A Windows-runneren a meglévő VSS-teszt (ahol a writer valóban nem válaszol): az eredmény `warning`, nem `ok` — a tesztet ehhez igazítsd (ne gyengítsd: ha a runneren a writerek válaszolnak, `ok` is elfogadható, de a tartalék esetén **kötelező** a `warning`).

---

## 3. (CI) A kihagyott Windows-tesztek ne adjanak zöld CI-t

### Hiba
A `LocalDbFact` kihagyja magát, ha a LocalDB nem érhető el; a kihagyás zöld CI-t ad. Így nem bizonyított, hogy az SQL → restic lánc integrációs tesztje a CI-ban valóban lefutott. Ugyanez igaz minden platform- vagy környezetfüggő kihagyásra a Windows-jobban.

### Javítás
- `BF_REQUIRE_LOCALDB=1` (a `BF_REQUIRE_RESTIC` mintájára): ha be van állítva és a LocalDB nem érhető el, a teszt **bukjon** (ne kihagyás legyen). A Windows-CI ezt állítsa be.
- A Windows-job „Test” lépése után egy ellenőrző lépés: a trx-ek alapján a kihagyott tesztek száma a Windows-jobban legyen 0, kivéve egy explicit, kommentezett engedélylistát (`tests/ci-allowed-skips.txt`, indoklással soronként). A Linux-jobban a `WindowsOnly*` kihagyások engedettek.
- A CI-összefoglalóba (`$GITHUB_STEP_SUMMARY`) kerüljön ki jobonként: lefutott / sikeres / kihagyott teszt, és név szerint az SQL LocalDB-teszt eredménye.

---

## 4. Nitek (ugyanebben a körben, ha kevés munka)

- **4a. `versioning.keep_days = 0`**: a validátor engedi (0–3650), de 0 esetén a megőrzés a **most készült** időbélyeg-mappát is azonnal törli (cutoff = most) — a verziózás gyakorlatilag törlés lesz. Legyen a minimum 1 (aki nem kér verziót, `versioning.enabled = false`). Teszt.
- **4b. Eltávolítás `/PURGE`-dzsal**: a `SetupHelper` a `DataPaths`-t használja, ami a `BACKUPFORGE_DATA` környezeti változót is figyeli. Ha ez gépszinten be van állítva (fejlesztői gép), a `/PURGE` azt a mappát törölné rekurzívan. A telepítő-segéd **ne** vegye figyelembe a környezeti változót (mindig `%ProgramData%\BackupForge`), és csak akkor töröljön, ha a mappában ott van a saját jelölője (`config.json` vagy `history.db`). Teszt.
- **4c. Dokumentáció**: a `docs/MANUAL-TESTS.md`-be egy lépés: „két feladat ugyanarra a NAS-megosztásra, az egyik a gyökérbe — a GUI nem engedi menteni” (az 1. pont ellenőrzése a felületen).

---

## Elfogadás
- A fenti tesztek zöldek, a CI zöld (Linux + Windows), a Windows-jobban 0 nem engedélyezett kihagyott teszt, és a step summaryben látszik, hogy a LocalDB-teszt lefutott.
- `CLAUDE.md`: „CR-001 — KÉSZ (dátum)” blokk (mi változott, hol), eltérések, és a „Következő lépés” frissítve (PR a `main`-re → kézi tesztek).
- A `docs/SPEC.md`-t ne írd át; ha egy viselkedés eltér a spec-től, az eltérés-szakaszba kerüljön.

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

## Eltérések a spec-től (F0)
- **Ág**: az AGENT-RUN `main`-t ír, de ez a munkamenet a környezet előírása szerint a `claude/blissful-goldberg-vlt55t` ágra pusholhat (más ágra nem). Minden fázis ide megy; a `main`-be olvasztás (PR) a user dolga.
- `config.json`-ban az ismeretlen mező hiba (nem csendben eldobott) — egy elgépelt kulcs (pl. `enabeld`) ne kapcsoljon ki csendben semmit.
- A `versioning.folder` nem lehet a tükörmappán belül (a célgyökérbe mentő feladatnál sem relatív név): a tükör különben a saját Versions mappáját törölné / verziózná. Célgyökérbe mentésnél tehát abszolút, a tükrön kívüli út kell.
- Azonosítók (job / target / repo): `^[a-z0-9][a-z0-9_-]{0,63}$` (útvonalban és restic-tagben is szerepelnek).
- Hosszú útvonalak: a .NET (Core) Windowson magától `\\?\`-es útra vált, ezért a kód nem tesz elé előtagot; a > 260 karakteres teszt a Windows-runneren fut (F2).
- `BackupForge.Windows` `net10.0` célú (nem `net10.0-windows`), `[SupportedOSPlatform("windows")]`-szal, hogy a solution Linuxon is egyben forduljon; a hívó oldalon `OperatingSystem.IsWindows()` őrzi.

## Következő lépés
- **F1** (SPEC 17.): szolgáltatás-host, ütemező (DST-tesztekkel), feladatsor + zárak + láncok teszt-motorral, `history.db`, IPC alap (named pipe), CLI alap.
- A restic-hash rögzítése: az első Windows-CI futás „Fetch restic” lépésének kimenetéből (`zip_sha256`, `exe_sha256`) → `third_party/restic/VERSION`, utána a lépés ne legyen `continue-on-error`.

## Döntésre vár (user)
- Licenc: MIT (SPEC 18.2 alapértelmezés) — `LICENSE` fájl az F7-ben kerül a repóba.
- restic-verzió: **0.20.0** (2026-11-19, a legfrissebb stabil; biztonsági javításokat tartalmaz). Ha konzervatívabb kell: 0.19.1.

## CI-ben / kézzel ellenőrizendő
- (még nincs)

## Ismert buktatók
- A konténerből a `github.com` (más repók, release-letöltés) nem érhető el → a restic forrását / dokumentációját a Go-proxyból lehet letölteni (`https://proxy.golang.org/github.com/restic/restic/@v/v0.20.0.zip`), a Windows-binárist csak a CI tölti le.
- `dotnet` helyben: `~/.dotnet` (dotnet-install.sh, 10.0 csatorna); `export PATH=$HOME/.dotnet:$PATH`.

## Nyitott kérdések
- Lásd SPEC 18. (név, licenc, kódaláírás, .NET 10 + Server 2016, GUI-keretrendszer, ügyfél-nézet, FFS/Cobian-importáló). Mindegyikhez van alapértelmezés, ezekkel lehet haladni.

## Kapcsolódó: BackupPulse
- Repó: `Craftzone-IT/BackupPulse` (PHP szerver + PowerShell / bash agentek). Ott van a meglévő FFS / Cobian / Veeam / sql_backup figyelés és a valós ügyféladatokból szerzett tapasztalat (FFS-lognevek, Cobian-napló, SQL-mentő script hibái) — érdemes elolvasni a `CLAUDE.md`-jét, ha egy viselkedés (pl. mit lát a SYSTEM-fiók, PS 5.1 buktatók) kérdéses.
- Az integráció (F8) a BackupPulse repóban egy külön CR lesz (`CR-010-backupforge.md` vagy a következő szabad szám), a STATUS-CONTRACT 4. pontja alapján.

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
- **2026-11-24 — F0 előtt.** A repó csak dokumentációt tartalmaz: SPEC v1, STATUS-CONTRACT, DECISIONS, BUSINESS. Kód még nincs.

## Következő lépés
- **F0** (SPEC 17.): solution-szerkezet, CI (Linux + Windows-runner), `Core` konfigmodell + validálás + `schema/config.schema.json`, naplózás, absztrakciók. Utána F1.

## Döntésre vár (user)
- (még nincs)

## CI-ben / kézzel ellenőrizendő
- (még nincs)

## Ismert buktatók
- (még nincs)

## Nyitott kérdések
- Lásd SPEC 18. (név, licenc, kódaláírás, .NET 10 + Server 2016, GUI-keretrendszer, ügyfél-nézet, FFS/Cobian-importáló). Mindegyikhez van alapértelmezés, ezekkel lehet haladni.

## Kapcsolódó: BackupPulse
- Repó: `Craftzone-IT/BackupPulse` (PHP szerver + PowerShell / bash agentek). Ott van a meglévő FFS / Cobian / Veeam / sql_backup figyelés és a valós ügyféladatokból szerzett tapasztalat (FFS-lognevek, Cobian-napló, SQL-mentő script hibái) — érdemes elolvasni a `CLAUDE.md`-jét, ha egy viselkedés (pl. mit lát a SYSTEM-fiók, PS 5.1 buktatók) kérdéses.
- Az integráció (F8) a BackupPulse repóban egy külön CR lesz (`CR-010-backupforge.md` vagy a következő szabad szám), a STATUS-CONTRACT 4. pontja alapján.

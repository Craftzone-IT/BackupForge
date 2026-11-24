# Felügyelet nélküli futtatás – utasítás Claude Code-nak

> Ezt a fájlt a végrehajtó (Claude Code) olvassa minden munkamenet elején, ha a feladata: „haladj a BackupForge fejlesztésével felügyelet nélkül”. A user nincs jelen, kérdésre nem válaszol. A cél: **minél több fázist végigvinni a SPEC 17. pontjából, megbízhatóan, úgy, hogy bármikor megszakadhasson, és a következő munkamenet folytatni tudja.**

## 1. Indulás (minden munkamenet elején)
1. Olvasd el: `CLAUDE.md` → `docs/SPEC.md` → `docs/STATUS-CONTRACT.md` → `docs/DECISIONS.md`.
2. A `CLAUDE.md` „Állapot” és „Következő lépés” szakaszából állapítsd meg, hol tart a munka. **Onnan folytasd** — a kész fázisokat ne kezdd újra.
3. `git pull`, és nézd meg a legutóbbi CI-futás eredményét (`gh run list --limit 5`). Ha piros, **először azt javítsd**.

## 2. A munka sorrendje
- Fázisok sorban: **F0 → F1 → F2 → F3 → F4 → F5 → F6 → F7**. Az F8 egy másik repóban van, azt **ne** csináld.
- Egy fázison belül kis lépésekben haladj: minden önálló, működő részlet után commit (pl. `F2: compare + plan`, `F2: versions retention`). Fázis közben is pusholj legalább egy-két óránként, hogy semmi ne vesszen el.
- **Egy fázis akkor kész**, ha:
  1. a SPEC 17. táblázatának „Kész, ha” oszlopa teljesül;
  2. `dotnet build` (warnings as errors) és `dotnet test` zöld helyben;
  3. a CI zöld **Linuxon és a Windows-runneren is**;
  4. a `CLAUDE.md` frissítve (lásd 5. pont).
- Utána azonnal kezdd a következő fázist. Ne állj meg „összefoglalni”, és ne várj jóváhagyásra.

## 3. Döntések felhasználó nélkül
- Ha a spec egyértelmű: kövesd.
- Ha a spec hiányos, vagy a valóság eltér tőle (API, csomag, platform): válaszd a **biztonságosabb és egyszerűbb** megoldást, és írd be a `CLAUDE.md` „Eltérések a spec-től (Fx)” szakaszába (mit, miért).
- Ha üzleti vagy felhasználói döntés kellene: a SPEC 18. pont alapértelmezését használd. Ha ott sincs válasz, válassz egy ésszerű alapértelmezést, haladj tovább, és vedd fel a `CLAUDE.md` **„Döntésre vár (user)”** listájába (mit döntöttél ideiglenesen, mi a másik lehetőség). **Ne állj meg kérdezni.**
- Ha egy részfeladat a környezet miatt itt nem végezhető el (pl. nincs Windows, nincs SQL Server, a hálózat blokkol egy letöltést): (a) amit lehet, CI-ban (Windows-runner) ellenőrizz; (b) ami ott sem megy, arra írj kézi tesztlépést a `docs/MANUAL-TESTS.md`-be; (c) jelöld a `CLAUDE.md`-ben „CI-ben / kézzel ellenőrizendő”-ként; (d) **haladj tovább** a következő részfeladattal vagy fázissal, ha az nem függ tőle.
- Ha egy fázis valóban elakad (pl. egy kötelező függőség nem érhető el, és nincs elfogadható kerülőút): dokumentáld az okot és a lehetséges megoldásokat a `CLAUDE.md`-ben, és lépj a következő **független** fázisra (pl. az F4 restic-motor nem függ az F6 GUI-tól). Teljesen csak akkor állj le, ha már semmi nem haladhat.

## 4. Tilos (felügyelet nélkül sem, semmilyen indokkal)
- `git push --force`, a történet átírása, ágak törlése. Mindig a `main`-re dolgozz, normál pusholással.
- Tag vagy GitHub Release létrehozása (`v*` tag → release a user dolga).
- A repó beállításainak, a GitHub Actions-secreteknek, a branch-védelemnek a módosítása.
- Titok (jelszó, token, kulcs) commitolása — tesztekhez is csak nyilvánvalóan hamis értékek (`test-password-123`).
- Fájltörlés a repón kívül; tesztekben csak a teszt saját ideiglenes mappájában írhatsz és törölhetsz.
- A tesztek gyengítése a zöld CI kedvéért (kikapcsolás, `Skip`, az elvárás lazítása). Ha egy teszt csak Windowson futhat, jelöld platformfeltétellel (pl. egy `[WindowsOnlyFact]` attribútum), ne töröld.
- A `docs/SPEC.md`, `docs/STATUS-CONTRACT.md`, `docs/DECISIONS.md` **tartalmi** átírása. Ezek a chat dokumentumai. Elírást javíthatsz; minden mást az eltérés-szakaszba írj. Kivétel: a `schema/*.json` a STATUS-CONTRACT-ból készül, és annak meg kell felelnie.
- A BackupPulse repó módosítása.
- **A repó nyilvános**: valódi ügyfél-, cég-, gép-, adatbázis- vagy személynév, belső IP-cím / hosztnév, e-mail-cím, valódi napló vagy képernyőkép nem kerülhet bele (kódba, tesztbe, fixture-be, dokumentációba, commit-üzenetbe). Példákhoz semleges neveket használj: `acme`, `SRV01`, `erp`, `example.com`, `192.168.1.10`.

## 5. A `CLAUDE.md` karbantartása (ettől függ a folytathatóság)
Minden fázis végén, és hosszabb fázisnál közben is (legalább a push-okkal együtt):
- **Állapot**: fázisonként egy blokk: „Fx — KÉSZ (dátum)” vagy „Fx — FOLYAMATBAN: kész: …; hátra: …”. Röviden: mi készült, hol van a kódban (fő osztályok, fájlok), hány teszt, mi zöld a CI-ban.
- **Eltérések a spec-től (Fx)**: mit és miért.
- **Döntésre vár (user)**: az ideiglenes döntések listája.
- **CI-ben / kézzel ellenőrizendő**: ami itt nem volt tesztelhető.
- **Következő lépés**: pontosan hol folytassa a következő munkamenet (fázis, részfeladat, ha van: félkész ág vagy fájl).
- **Ismert buktatók**: amibe belefutottál (csomag-inkompatibilitás, platformkülönbség, CI-trükk) — hogy a következő munkamenet ne fusson bele újra.

## 6. Technikai útmutató
- **.NET SDK**: ha nincs telepítve, a `dotnet-install` szkripttel telepítsd (a 10-es LTS-csatorna). A `global.json` rögzítse az SDK-verziót.
- **CI** (`.github/workflows/ci.yml`): `ubuntu-latest` (build + a platformfüggetlen tesztek) és `windows-latest` (build + minden teszt + a Windows-integrációs tesztek). A repó jelenleg nyilvános, a szabványos runnerek így ingyenesek — a Windows-job mehet minden pushra. Legyen `concurrency` (`cancel-in-progress: true`), hogy egy új push leállítsa az elavult futást; és a workflow ne függjön attól, hogy a repó nyilvános (később privát lehet). Push után várd meg és ellenőrizd: `gh run watch` / `gh run view --log-failed`. A hibát javítsd, mielőtt továbblépsz.
- **restic**: a build / CI a `third_party/restic/VERSION` szerint tölti le a hivatalos release-ből, és SHA-256-tal ellenőrzi. Ha helyben a letöltés a hálózat miatt nem megy, a restic-integrációs teszteket csak a CI futtassa (a fixture-alapú unit tesztek helyben is fussanak).
- **SQL Server a CI-ban**: nézd meg, van-e a `windows-latest` képen SQL Server / LocalDB, vagy telepíthető-e elfogadható időn belül (pl. SQL Server Express csendes telepítés, vagy egy kész GitHub Action). Ha igen: az F5 integrációs tesztje ott fusson. Ha nem: fixture- és mock-alapú unit tesztek + `docs/MANUAL-TESTS.md`.
- **VSS, szolgáltatás, named pipe, DPAPI, WNet**: a Windows-runneren integrációs tesztekkel (a runner adminként fut). Az SMB-hitelesítéses célt a CI-ban egy helyi megosztással és helyi felhasználóval lehet modellezni (`New-SmbShare` + `net user`), ha működik; ha nem, kézi teszt.
- **WPF (F6)**: a CI-ban csak build és a ViewModel-ek unit tesztjei. A GUI-folyamatokat a `docs/MANUAL-TESTS.md`-ben írd le lépésenként. Használj MVVM-et (CommunityToolkit.Mvvm), hogy a logika GUI nélkül tesztelhető legyen.
- **Inno Setup (F7)**: a CI Windows-runneren (`choco install innosetup` vagy a runner-képen meglévő példány) építse a telepítőt, és **artifactként** töltse fel (Release-t ne hozzon létre). A csendes telepítést és eltávolítást a CI-ban is futtasd le egy füsttesztként (telepítés → a szolgáltatás fut → `bf status` → eltávolítás → a szolgáltatás eltűnt, a célmappa érintetlen).
- Teljesítmény és robusztusság: fájlmotornál legyen teszt nagy fájlszámra (pl. 10 000 kis fájl) és hosszú útvonalra (> 260 karakter, Windows-runneren).

## 7. A munkamenet vége
Ha elfogy az idő vagy a kontextus, vagy minden elérhető fázis kész: commit + push, a `CLAUDE.md` „Következő lépés” naprakész. Az utolsó üzenetedben (a user később olvassa) röviden: mely fázisok készültek el, mi a CI-állapot, mi vár döntésre, mit kell kézzel tesztelni.

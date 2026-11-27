# Kézi tesztlista (SPEC 15. / 16.)

Tiszta Windows Server 2019/2022 VM-en (vagy Windows 10/11-en), rendszergazdaként. Minden lépésnél: **elvárt eredmény** dőlt betűvel. A CI-ban automatikusan futó részeket (szolgáltatás telepítése, fájlmentés VSS-sel, restic, LocalDB-s SQL-lánc, csendes telepítés/eltávolítás füstteszt) itt csak a valós környezetre jellemző pontokon ismételjük.

## A. Telepítés, frissítés, eltávolítás (F7)
1. `BackupForge-Setup-<ver>.exe` dupla kattintás → nyelv, licenc (MIT + restic BSD-2), hely, „Konfigurátor indítása”. *A szolgáltatás `BackupForge` fut, automatikus (késleltetett) indítás; `sc qsidtype BackupForge` → UNRESTRICTED; `sc qfailure BackupForge` → 3× restart 60 000 ms, reset 86400; a Start menüben „BackupForge” ikon; az eseménynaplóban 1000-es esemény.*
2. `C:\ProgramData\BackupForge` jogosultságai: *öröklés nélkül SYSTEM + Administrators teljes, Users semmi; a `secrets.dat` csak SYSTEM.*
3. Csendes telepítés: `setup.exe /VERYSILENT /SUPPRESSMSGBOXES /CONFIG="acme.json" /SECRETS="acme.secrets.json"`. *A konfig a helyén, a titkok a `secrets.dat`-ban, a telepítő naplója figyelmeztet: a secrets-fájlt törölni kell; a secrets-fájl nem törlődött.*
4. Frissítés ugyanazzal a telepítővel egy újabb verzióra. *A konfig, az előzmények és a titkok megmaradnak; a szolgáltatás újraindul.*
5. Eltávolítás interaktívan: a „Megtartod a beállításokat?” kérdésre Igen. *A szolgáltatás és a programmappa eltűnik, a `ProgramData\BackupForge` marad; a mentési célokhoz, a Versions mappákhoz, a restic-tárolókhoz nem nyúl.*
6. Csendes eltávolítás `/PURGE`-dzsel. *A `ProgramData\BackupForge` is törlődik; a mentési célok érintetlenek.*

## B. Szolgáltatás és futások
1. Fájlmentés futás közben `Restart-Service BackupForge`. *A futás `interrupted`, a célon nem marad `.bfpart` a következő futás után; az eseménynaplóban nincs árva VSS-pillanatkép (`vssadmin list shadows` üres a BackupForge-tól).*
2. Gép újraindítása egy ütemezett időpont alatt (kikapcsolt állapotban múlik el). *Indulás után 2 perccel egyszer lefut (`catch_up` a `bf history`-ban).*
3. A forrásmappa átnevezése / kiürítése. *A futás `failed` („a forrás üres / nem elérhető”), a célon semmi nem törlődik, CRIT e-mail.*
4. A forrás 60 %-ának törlése. *`failed` „törlési biztosíték …”, semmi nem törlődik, 2003-as esemény; a GUI Áttekintés „Tömeges törlés egyszeri jóváhagyása” gombja után a következő futás töröl (a Versions-be).*
5. Hálózati cél megszakadása futás közben (NAS kihúzása). *A futás azonnal `failed` „a cél nem elérhető”.*
6. SMB-cél saját felhasználóval (`username` + jelszó a GUI-ban), meghajtóbetű nélkül; „Kapcsolat tesztelése”. *Elérhető, a teszt fiókja SYSTEM; ha ugyanarra a szerverre már van más hitelesítésű kapcsolat: érthető 1219-es üzenet.*
7. Zárolt fájl (pl. nyitott Outlook PST) VSS-sel. *A fájl a tükörbe kerül; `vss: never` mellett kihagyva, warning.*

## C. GUI-folyamatok (CR-002: varázsló)
1. **Új fájlfeladat új SMB-céllal** (16.1): Feladatok → „Új feladat…” → Fájlmentés → Forrás: „Mappa hozzáadása…” (`D:\Company`), „Ellenőrzés a szolgáltatás nevében” → Tovább → Cél: „Új cél…” → Hálózati megosztás, `\\NAS01\backup`, felhasználó + jelszó, „Kapcsolat tesztelése” (*„elérhető és írható, N GB szabad (fiók: SYSTEM)”*) → OK → a tükör és a Versions végső útja kiírva → Tovább → Mód: pontos tükör, 21 nap, VSS ajánlott → Ütemezés: N óránként, 4, 06:15 (*„4 óránként 06:15-től” és a következő 5 futás*) → Név: „Company → NAS01” (az azonosító a névből) → Összegzés (*egy-két emberi mondat*) → Próbafuttatás (*terv, a célon nem változik semmi, a konfig sem*) → „Befejezés és futtatás most”. *A feladat a táblában, a kártya haladást mutat, a végén ok; módosított fájl után a `<subdir>_Versions\<időbélyeg>` mappában a régi változat.*
2. **Forrás csatolt meghajtóbetűről**: a saját munkamenetben `Z:` = `\\fs01\data` → forrásnak `Z:\Projektek` → „Ellenőrzés…”. *Figyelmeztetés: a szolgáltatás nem látja, „Legyen ez: \\fs01\data\Projektek” gomb; rákattintva az UNC-út kerül be.*
3. **SQL-feladat „tárolóba is” lánccal** (16.1): Új feladat → SQL Server-mentés → Példány (felderítésből, `.\SQLEXPRESS`) → ha nincs jog: „Jogosultságok beállítása…” (a T-SQL előbb megjelenik) → Adatbázisok: minden felhasználói (*FULL recovery-s db-n figyelmeztetés*) → Beállítások: 2 helyi példány, ellenőrzés, hetente CHECKDB, „A mentéseket utána tárolóba is küldöm: offsite” (ha nincs tároló: „Új tároló…” → generált jelszó, helyreállítási lap nyomtatása, init csak megerősítés után) → Ütemezés: naponta 22:30 → Befejezés. *Két feladat jön létre: az SQL és a `<id>-restic` (forrás `@staging:<id>`, „a(z) … minden sikeres futása után”); „Futtatás most” az SQL-en → utána a restic ugyanazzal a láncazonosítóval (Előzmények).*
4. **Szerkesztés**: egy meglévő feladat → Szerkesztés… → a lépések kitöltve, a Típus lépés nincs → Befejezés változtatás nélkül. *A `config.json` bájtra azonos (a `config_changes`-ben nincs új sor).* Utána a nevet és az ütemezést módosítva → Befejezés → *a táblában az új név és ütemezés.*
5. **Speciális lap**: „Speciális…” bármelyik lépésről → *minden mező emberi címkével és rövid súgóval, nyers kulcsnév legfeljebb a súgóban*; az azonosító átírása → *figyelmeztetés: az előzmények a régi néven maradnak.*
6. **Hibás piszkozat**: Szerkesztés… → Cél: a célgyökér (üres almappa) egy másik feladat mellett (CR-001) → *a Cél lépésen azonnal emberi hiba („… átfedi …”)*; az Összegzésen a hibára kattintva a Cél lépés nyílik meg; a Befejezés tiltva. A Mód lépésen biztonsági küszöb 0 → *„Tovább” tiltva, „A biztonsági küszöb 1 és 100 % között lehet.”*
7. **Mégse**: bármelyik lépésen Mégse → megerősítés → *a konfig nem változik.*
8. **Visszaállítás** (16.6): Visszaállítás fül → *a Feladat lista kitöltve (H1)* → fájlfeladat → Versions-időpont → mappa → fájl → célmappa → Visszaállítás. *A fájl a `<cél>\Restore_<időbélyeg>` alatt; ugyanabban a másodpercben egy második visszaállítás `… (2)` mappába kerül (H5).* Restic-feladatnál snapshotból is.
9. **Célok és tárolók**: Új cél → *az útvonal mező üres, a minta (`\\server\share\folder`) szürke vízjel*; Helyi mappa / Hálózati megosztás választó, felhasználó és jelszó csak hálózatinál. Restic-tároló, ami nincs inicializálva → *„nincs inicializálva” jelvény + „Létrehozás…”.*
10. Feladat letiltása a táblában → *azonnal mentve; Áttekintésen „Letiltott feladat … óta”, a `status.json`-ban `enabled: false` + `disabled_since`, a heti riportban külön szakasz.* Törlés → *a megerősítés kimondja: az előzmények maradnak, a célon semmi nem törlődik.*
11. Nyelv: Beállítások → en → Mentés → GUI újraindítása. *Angol felület és angol e-mailek.*
12. Export / import: export → a fájlban nincs jelszó; importálás egy másik gépen → *a hiányzó titkok listája megjelenik.*
13. **Két feladat ugyanarra a NAS-megosztásra, az egyik a gyökérbe** (CR-001): ld. 6. — *a gyökérbe mentő tükör nem menthető; `update` módban csak figyelmeztetés.*
14. Régebbi szolgáltatás (protokoll 1) mellett az új GUI → *„A szolgáltatás régebbi, mint ez a konfigurátor — futtasd újra a telepítőt.”*

## D. E-mail
1. Értesítések → SMTP (pl. M365, starttls) → jelszó → Teszt e-mail. *A levél megérkezik.*
2. Hibás futás → *HIBA-levél (tárgy: `[BackupForge][<név>] HIBA: <feladat> — <összesítő>`, HTML + szöveg, a futásnapló csatolva), ugyanaz a hiba 24 órán belül nem jön újra, a helyreállás után HELYREÁLLT-levél.*
3. Heti riport előnézete, majd a riport időpontjában a levél. *Outlookban (asztali és webes) helyesen jelenik meg: táblázat, színek, ékezetek.*
4. Rossz SMTP-jelszó. *3 újrapróbálás (1, 5, 15 perc) után 2020-as esemény és a `status.json` `notifications.last_error`.*

## E. SQL (ha a CI-ban nem volt LocalDB)
1. SQL Server Express-en: a `bf sql grant <job> --yes` után a szolgáltatás (`NT SERVICE\BackupForge`) ment; új adatbázis létrehozása után *az új db `failed` „nincs jogosultság (új adatbázis?)”*, grant után rendben.
2. `checkdb` ütemezés → *külön `checkdb` futás az előzményben.*
3. Betelt staging-kötet → *„nincs elég hely az átmeneti mappában”, BACKUP el sem indul.*

## F. CR-002 javítások (valós szerveren)
1. **VSS writerekkel** (A1): emelt jogú PowerShellben `bf diag vss C:\Users` → *a kimenet minden lépés HRESULT-ját és aszinkron státuszát mutatja, writerek száma > 0, `GetSnapshotProperties` „matches”, `status 12 (CREATED)`, eszközútvonal, „read N byte(s)”, `RESULT: ok`, kilépési kód 0.* Utána egy `vss: when_available` fájlfeladat egy zárolt fájllal (nyitott PST, vagy PowerShellből `[IO.File]::Open($f, 'Open', 'ReadWrite', 'None')`) → *a futás `ok` (nem warning), a zárolt fájl a tükörben.* Ha mégis „no device object”: a futásnaplóban ERR szintű „VSS diagnostics:” blokk — ezt kell továbbítani.
2. **SMB próba + futás versenyhelyzet** (A2): SMB-cél saját felhasználóval, ötpercenkénti ütemezés, legalább 30 percig (a célpróba is 5 percenként fut). *Egyetlen 1219-es hiba sincs; a futások `ok`-k; a futások között `Get-SmbConnection` / `net use` szerint a kapcsolat nem marad meg (a szolgáltatás bontja, ha már senki nem használja).*
3. **Leállítás nagy fájl közben** (A3): egy 1 GB-nál nagyobb fájl másolása közben `Restart-Service BackupForge`. *A leállás < 5 mp, a futás `interrupted`, `.bfpart` nem marad, a szolgáltatásnaplóban nincs „did not stop within 00:01:00”.*
4. **Restic-visszaállítás Windows-úttal** (A4): `bf restore <restic-job> --from <snap> --path "C:\data\docs\file1.txt" --to C:\restore-test` → *`ok`, „1 fájl”*; nem létező útra → *`failed` „nincs ilyen útvonal a pillanatképben: …”.*
5. **ACL és kódolás** (A6, A7): `icacls C:\ProgramData\BackupForge\secrets.dat` → *csak `NT AUTHORITY\SYSTEM:(F)`*; `bf status > out.txt` → *az ékezetek UTF-8-ként helyesek*; `Get-Content C:\ProgramData\BackupForge\setup.log` (PS 5.1) → *a „—” helyesen jelenik meg*; az Alkalmazás-naplóban nincs 0-s azonosítójú BackupForge-esemény.
6. **Telepítő mérete és frissítés** (A8): *a telepítő ~58 MB (korábban ~114 MB)*; frissítés egy régi (single-file) telepítésről → *a szolgáltatás fut, a konfig és az előzmények maradnak.*

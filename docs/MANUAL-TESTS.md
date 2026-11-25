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

## C. GUI-folyamatok (F6)
1. **Fájlmentés NAS-ra Versions-szel** (16.1): Célok → új SMB-cél, jelszó, teszt → Feladatok → „+ files”, forrás, cél, ütemezés („4 óránként 06:15-től” előnézet), Mentés → Próbafuttatás (terv a táblában) → Áttekintés „Futtatás most”. *A kártya haladást mutat, a végén ok; módosított fájl után a `<subdir>_Versions\<időbélyeg>` mappában a régi változat.*
2. **SQL Express → restic lánc** (16.1): SQL → Példányok felderítése → Jogosultságok beállítása (a T-SQL előbb megjelenik) → Célok és tárolók → új restic-tároló („Új tároló…”: generált jelszó, helyreállítási lap nyomtatása) → Feladatok: sql feladat `on_success`-szel, restic feladat `@staging:<sql>` forrással, `chained` ütemezés → Mentés → „Futtatás most” az SQL-en. *Az SQL után a restic automatikusan lefut, ugyanazzal a láncazonosítóval (Előzmények); FULL recovery-s adatbázisra figyelmeztetés.*
3. **Visszaállítás** (16.6): Visszaállítás fül → fájlfeladat → Versions-időpont → mappa megnyitása → egy fájl → célmappa → Visszaállítás. *A fájl a `<cél>\Restore_<időbélyeg>` alatt, a régi tartalommal.* Ugyanez egy mappára, és restic-feladatnál snapshotból. Eredeti helyre: megerősítés után a felülírt fájl a `<forrás>_RestoreBackup_<időbélyeg>` mappában.
4. Hibás mező (pl. delete_guard_pct = 0) → Mentés. *A mező melletti piros szöveg mutatja a hibát, a konfig nem változik.*
5. Feladat letiltása → Mentés. *Áttekintésen „Letiltott feladat … óta”, a `status.json`-ban `enabled: false` + `disabled_since`, a heti riportban külön szakasz.*
6. Nyelv: Beállítások → en → Mentés → GUI újraindítása. *Angol felület és angol e-mailek.*
7. Export / import: export → a fájlban nincs jelszó; importálás egy másik gépen → *a hiányzó titkok listája megjelenik.*
8. **Két feladat ugyanarra a NAS-megosztásra, az egyik a gyökérbe** (CR-001): Célok → egy SMB-cél (`\\NAS01\backup`) → két fájlfeladat, az egyik `subdir` nélkül (a megosztás gyökerébe, tükör módban, a Versions mappa máshol), a másik `Company` almappába → Mentés. *A GUI nem engedi menteni: a gyökérbe mentő feladat `target` mezője mellett „… átfedi a(z) 'company' tükörmappa útvonalát …” hiba; `update` módban csak figyelmeztetés jön, a mentés megtörténik.*

## D. E-mail
1. Értesítések → SMTP (pl. M365, starttls) → jelszó → Teszt e-mail. *A levél megérkezik.*
2. Hibás futás → *HIBA-levél (tárgy: `[BackupForge][<név>] HIBA: <feladat> — <összesítő>`, HTML + szöveg, a futásnapló csatolva), ugyanaz a hiba 24 órán belül nem jön újra, a helyreállás után HELYREÁLLT-levél.*
3. Heti riport előnézete, majd a riport időpontjában a levél. *Outlookban (asztali és webes) helyesen jelenik meg: táblázat, színek, ékezetek.*
4. Rossz SMTP-jelszó. *3 újrapróbálás (1, 5, 15 perc) után 2020-as esemény és a `status.json` `notifications.last_error`.*

## E. SQL (ha a CI-ban nem volt LocalDB)
1. SQL Server Express-en: a `bf sql grant <job> --yes` után a szolgáltatás (`NT SERVICE\BackupForge`) ment; új adatbázis létrehozása után *az új db `failed` „nincs jogosultság (új adatbázis?)”*, grant után rendben.
2. `checkdb` ütemezés → *külön `checkdb` futás az előzményben.*
3. Betelt staging-kötet → *„nincs elég hely az átmeneti mappában”, BACKUP el sem indul.*

# CR-002 — A kézi tesztek javításai + GUI-varázsló

**Állapot: NYITOTT (2026-11-27)**

Forrás: `docs/TEST-FINDINGS.md`, a T1 (Windows 11, GUI) és a T2 (tiszta Windows Server 2022 VM, SQL Server 2025 Express, SMB-cél, CLI) kézi tesztjei. A telepítés, a fájl-, a restic- és az SQL-motor nagyrészt rendben van. Két hiba viszont a mentés helyességét érinti: a **VSS gyakorlatilag soha nem működik (H2)**, és **az SMB-cél versenyhelyzetben 1219-cel elszáll (H3)**. A GUI teszteléshez elég, de felhasználónak nem: nyers kulcsneveket mutat, és a célt kézzel kell beírni.

A CR két része:
- **A rész — javítások** (H2–H8, E1). Előbb ezt kell elvégezni, mert ez a mentés helyességét érinti.
- **B rész — GUI** (H1, varázsló, G1–G5).

Minden pont külön commit (`CR-002 A1: …`), mindegyikhez teszt kell. A CR akkor kész, ha:
- a CI zöld (Linux + Windows, 0 nem engedélyezett kihagyással);
- a `docs/MANUAL-TESTS.md` frissítve van;
- a `CLAUDE.md`-ben megvan a „CR-002 — KÉSZ” és az „Eltérések a CR-002-től” szakasz;
- a `TEST-FINDINGS.md` érintett tételei mellett ott a „→ CR-002 kész” jelölés.

A felügyelet nélküli munka szabályai (`docs/AGENT-RUN.md`) érvényesek, a repó továbbra is nyilvános.

**Ami itt nem ellenőrizhető:** a VSS writeres ága, a valódi SMB-hitelesítés és a GUI-folyamatok. Ezeket a chat ellenőrzi a saját tesztgépén (tiszta Windows Server 2022 VM) a CI telepítőjével, illetve a user a saját gépén. Ezek a pontok a `CLAUDE.md`-ben „ellenőrzésre vár (chat)” jelölést kapjanak. Ha egy pont kódja és tesztje kész, a végrehajtó lépjen tovább, ne várjon.

---

## A rész — javítások

### A1. (FONTOS) VSS: „snapshot has no device object” (H2)

**Tünet (T2).** Minden `vss: when_available` futás `warning`, ezzel az üzenettel: „a VSS-pillanatkép nem készült el (snapshot has no device object) — VSS nélkül mentve”. A gépen minden writer `Stable / No error`, és a `vssadmin create shadow /for=C:` hibátlanul működik.

**Miért nem fogta meg a CI.** A hosztolt runneren a `GatherWriterMetadata` sosem fejeződik be, ezért ott **mindig a writer nélküli tartalékág fut** (`CreateOnce(withWriters: false)`). A writeres ág (`GatherWriterMetadata` → `PrepareForBackup` → `DoSnapshotSet` → `GetSnapshotProperties`) **egyetlen gépen sem futott még le sikeresen**. A hiba tehát valószínűleg a writeres ágban van, vagy a `DoSnapshotSet` / `GetSnapshotProperties` párosban, ami a tartalékágban is szerepel.

**Amit a chat már átnézett.** Az `IVssBackupComponents` vtable-sorrendje a `vsbackup.h` szerint a `GetSnapshotProperties`-ig egyezik. A `VssSnapshotProp` elrendezése is megfelel a `VSS_SNAPSHOT_PROP`-nak. Ez alapján nem egyértelmű a hiba.

**Lépések:**
1. **Diagnosztika: `bf diag vss [<útvonal>] [--no-writers]`** (helyben, emelt jogú `bf`-ből, a szolgáltatás nélkül). A folyamat elején ugyanúgy hívja az `InitializeComSecurity()`-t, mint a szolgáltatás. Lépésenként írja ki:
   - a HRESULT-ot, az `IVssAsync.QueryStatus` minden visszaadott státuszát és a lépés idejét;
   - a `GatherWriterMetadata` után a writerek számát;
   - a `DoSnapshotSet` után a `GetSnapshotProperties` HRESULT-ját és a kitöltött `VSS_SNAPSHOT_PROP` nyers bájtjait hexában;
   - a dekódolt mezőket: `SnapshotId` (egyezik-e a kérttel), `SnapshotsCount`, `Status` (várt: `VSS_SS_CREATED` = 12), és az eszközútvonal, ha van;
   - ha van eszközútvonal: a megadott útvonal egy fájlja olvasható-e a pillanatképből (pl. az első fájl első 4 KB-ja).

   A végén takarít (`DeleteSnapshots`, mint a `Session.Dispose`). Kilépési kód: 0, ha az eszközútvonal megvan és olvasható; 1 egyébként. A kimenetben ne legyen gépnév.
2. **Ellenőrzendő hipotézisek** (a diagnosztika ezekre ad választ; ami kódból kiderül, azt javítsd rögtön):
   - A `Wait` a `QueryStatus` 0-s (`S_OK`) státuszát kész-nek veszi. Valójában csak a `VSS_S_ASYNC_FINISHED` jelenti a sikeres befejezést. Ha a `DoSnapshotSet` még nem fejeződött be, a pillanatkép tulajdonságai üresek.
   - Az `IVssAsync.Wait(1000)` időtúllépéskor milyen kódot ad. A ciklus ne kezelje ezt hibának, és ne kezelje befejezésnek se.
   - A `GetSnapshotProperties(Guid, IntPtr)`: a `Guid` érték szerinti átadása a `[ComImport]` interfészen x64-en. Ha a visszaadott `SnapshotId` csupa nulla, a hívás nem töltötte ki a struktúrát.
   - A `VssFreeSnapshotPropertiesInternal` csak sikeres ágon hívódik. Ez szivárgás, nem ez a hiba, de javítsd: `finally`-ba.
   - A sorrend a writeres ágban: a `FreeWriterMetadata` a `PrepareForBackup` előtt.
3. **Javítás.** A cél a saját interop rendbetétele. Ha a diagnosztika alapján sem oldható meg elfogadható időn belül, tartaléknak elfogadható egy második szolgáltató a `Win32_ShadowCopy.Create` WMI-hívással (writerekkel készül, eszközútvonalat ad). Ezt az `Eltérések` szakaszban indokolni kell.
4. **Csendes leromlás ellen.** A `when_available` → „VSS nélkül mentve” ág figyelmeztetése maradjon. Ha a pillanatkép elkészült, de **nincs eszközútvonal**, az a VSS-motor hibája, ezért a futásnapló Error szinten írja ki a teljes diagnosztikai összefoglalót (HRESULT-ok, státusz), ne csak az üzenetet.
5. **Tesztek.**
   - Linux: a `Wait`-ciklus státuszkezelése egy hamis `IVssAsync`-kel (`PENDING` → `FINISHED`, `S_OK`, időtúllépés, `CANCELLED`, hibakód). Ehhez a ciklust ki kell emelni egy platformfüggetlen segédbe.
   - Windows-CI: a meglévő VSS-tesztek mellé egy `bf diag vss --no-writers` futtatás, ami a runneren is 0-val lép ki, és a zárolt tesztfájl olvasható a pillanatképből.
   - A writeres ág CI-tesztje maradjon feltételes: ha a writerek válaszolnak, a pillanatképnek kötelező elkészülnie (eszközútvonal + olvasható zárolt fájl); ha nem válaszolnak, a tartaléknak kötelező `warning`-ot adnia. Sikeres futás figyelmeztetés nélkül csak akkor elfogadható, ha a zárolt fájl ténylegesen a pillanatképből jött.
   - `docs/MANUAL-TESTS.md`: új pont „VSS writerekkel, valós szerveren”: `bf diag vss C:\…` → 0; egy zárolt fájl (pl. nyitott PST, vagy egy `FileShare.None`-nal nyitva tartott fájl) bekerül a mentésbe, a futás `ok`.
6. **Ellenőrzés (chat, tesztgép).** A CI telepítőjével: `bf diag vss`, majd egy `when_available` fájlfeladat zárolt fájllal. Az eredmény a `TEST-FINDINGS.md`-be kerül.

### A2. (FONTOS) SMB-cél: 1219 versenyhelyzetben, a kapcsolat nem bontódik (H3)

**Tünet (T2).** Egy SMB-célnál saját felhasználóval egy futás `failed` lett „error 1219”-cel, ugyanabban a másodpercben, amikor a `TargetProbe` is 1219-et kapott. A futás után a kapcsolat megmarad (`Get-SmbConnection`).

**Ok (kódból).** A `WNetConnector.Connect` minden hívó (próba, futás, `target.test`, visszaállítás) saját `WNetAddConnection2` / `WNetCancelConnection2` párt hív, összehangolás nélkül. A 85-ös kódnál (`ERROR_ALREADY_ASSIGNED`) `NullDisposable`-t ad vissza, tehát valaki más kapcsolatát használja. Ha az a másik közben bont, a futás a cél alól kiesik. Ha két hívó ugyanarra a szerverre egyszerre kapcsolódik, 1219 jöhet.

**Javítás: egy kapcsolatkezelő, `SmbConnectionManager`, a `BackupForge.Windows`-ban.**
- Kulcs: a szerver neve (kisbetűvel), mert az 1219 szerverenként jön, nem megosztásonként.
- Szerverenként zár és referenciaszámláló. Az `Acquire(target)` egy lease-t ad (`IDisposable`).
- Ha a szerverhez már van **saját, ugyanazzal a felhasználóval** létrehozott kapcsolat, azt használja újra (számláló +1, nincs újabb WNet-hívás). Ha a meglévő saját kapcsolatot **más felhasználóval** hozta létre (közben megváltozott a konfig), az utolsó lease elengedése után bont, és újrakapcsolódik.
- Bontás (`WNetCancelConnection2`) csak a saját kapcsolatnál, az utolsó lease elengedésekor.
  - Ha a kapcsolatot nem a BackupForge hozta létre (85, vagy 1219 egy idegen munkamenet miatt), soha nem bontja.
  - 1219-nél az üzenet maradjon érthető, és nevezze meg a lehetséges okot (más hitelesítésű meglévő kapcsolat), jelszó nélkül.
- Minden hívó ezt használja: a futás, a `TargetProbe`, a `target.test`, a visszaállítás és a cél szabad helyének lekérdezése.
- A P/Invoke kerüljön egy `INativeWNet` mögé, hogy a számlálás Linuxon is tesztelhető legyen.

**Tesztek.**
- Linux (hamis `INativeWNet`): párhuzamos `Acquire` ugyanarra a szerverre → egyetlen `Add`, a bontás csak az utolsó elengedés után; idegen kapcsolat (85) → nincs bontás; felhasználócsere → bontás és újrakapcsolódás; kivétel a futásban → a lease felszabadul.
- Windows-CI, a meglévő helyi megosztással és helyi felhasználóval: 20 párhuzamos próba és egy futás ugyanarra a szerverre → nincs 1219, a futás `ok`; utána a `Get-SmbConnection` / `net use` szerint nincs megmaradt kapcsolat.

**Ellenőrzés (chat, tesztgép).** A T2 SMB-forgatókönyve 5 percenkénti próbával, legalább 30 percen át, ötpercenkénti futással.

### A3. Leállítás nagy fájl másolása közben ~70 mp (H6)
- A `FileCopier` a másolást darabokban végezze (pl. 4 MB), és minden darab után figyelje a `CancellationToken`-t. Megszakításkor a `.bfpart` törlődik (ez ma is így van), a futás `interrupted`.
- A cél: a szolgáltatás leállítása egy 1 GB-os fájl másolása közben < 5 mp alatt fejeződjön be (a türelmi idő 60 mp marad).
- A `throttle_mbps` token-bucket ugyanebben a ciklusban működjön tovább.
- Teszt: egy 256 MB-os ideiglenes fájl másolását a token 200 ms után megszakítja → < 2 mp alatt visszatér, `.bfpart` nem marad, a célfájl nem jön létre.

### A4. Restic-visszaállítás nem létező útvonalra: „ok, 0 fájl” (H4)
- A `bf restore` és a `restore.run` fogadja el a Windows-formájú útvonalat is (`C:\data\x`, `C:/data/x`), és alakítsa a snapshot belső formájára (`/C/data/x`). Egy közös függvény legyen a CLI-nek és a GUI-nak, tesztekkel.
- Ha a megadott útvonal a snapshotban nem létezik, az eredmény `failed`, ezzel az üzenettel: „nincs ilyen útvonal a pillanatképben: …”. Ellenőrzés a visszaállítás előtt `restic ls <snap> <út>`-tal, vagy a summary alapján, ha 0 fájl állt vissza és útvonal volt megadva.
- A „N fájl visszaállítva” csak a fájlokat számolja, a mappákat ne (a restic summary megfelelő mezője alapján).
- Teszt: valódi resticcel (a meglévő restic-integrációs tesztek mellé) Windows- és Unix-formájú útvonallal; nem létező útvonal → `failed`; egy fájl → „1 fájl”.

### A5. Visszaállítási mappanév-ütközés ugyanabban a másodpercben (H5)
- Ha a `Restore_<yyyy-MM-dd HHmmss>` már létezik, a név kapjon utótagot: `… (2)`, `… (3)`. Ez a fájl- és a restic-visszaállításra is vonatkozik.
- A „nem üres célba nem állítunk vissza” szabály maradjon, de a saját, frissen generált név ne ütközzön.
- Teszt: két visszaállítás ugyanazzal az órával (`ManualClock`) → két külön mappa.

### A6. `secrets.dat` ACL: csak SYSTEM (H7)
- A `--setup-install` (telepítő) és a szolgáltatás induláskor állítsa a `secrets.dat` ACL-jét öröklés nélkül **csak SYSTEM**-re (SPEC 14.). A futtató felhasználó hozzáadása csak akkor maradjon, ha a folyamat nem SYSTEM **és** nem a telepítő (fejlesztői / teszt mód).
- Indulási migráció: ha a meglévő fájl ACL-jében más is szerepel, a szolgáltatás javítsa ki, és írjon INFO-t a naplóba.
- Teszt (Windows-CI): csendes telepítés után az `icacls secrets.dat` kimenete csak SYSTEM; a telepítő-füstteszt (`installer/smoke-test.ps1`) is ellenőrizze.

### A7. Kódolás (H8)
- `bf.exe`: ha a kimenet át van irányítva (`Console.IsOutputRedirected` / `IsErrorRedirected`), UTF-8-at írjon (BOM nélkül). Interaktív konzolon az ékezetek ma is helyesek, ott ne változzon semmi. Teszt: a `bf` folyamatként indítva, átirányított kimenettel → az „á” bájtjai UTF-8-ak.
- `setup.log` és a szolgáltatás fájlnaplója: UTF-8 **BOM-mal** (a PS 5.1 `Get-Content`-je így olvassa helyesen). Hozzáfűzésnél csak új fájl kap BOM-ot.
- Eseménynapló: a 0-s eseményazonosítójú bejegyzések (a .NET alapértelmezett naplózója) ne kerüljenek az alkalmazásnaplóba. Csak a `docs/EVENT-IDS.md` szerinti azonosítók legyenek; az egyéb üzenetek a fájlnaplóba menjenek.

### A8. (ha belefér) A telepítő mérete > 100 MB (E1)
- Ma három self-contained single-file exe készül, mindegyikben a teljes .NET runtime (a GUI-ban a WPF is).
- Javaslat: egyetlen közös programmappa (`{app}`), **self-contained, de nem single-file**, a három projekt közös `--output`-tal és azonos RID-del (`win-x64`), így a runtime egyszer szerepel. A `restic.exe` és a licencek a helyükön maradnak.
- Mérd meg a mostani és az új telepítő méretét, és írd a `CLAUDE.md`-be. A telepítő-füstteszt maradjon zöld (frissítés a régi, single-file telepítésről is: a régi exe-k felülíródnak, a szolgáltatás indul).
- Ha a közös mappa bármi miatt nem működik (pl. eltérő runtime-igény a WPF miatt), maradjon a mostani megoldás, és az okot írd az `Eltérések` szakaszba.

---

## B rész — GUI

### B1. Üres legördülők a Visszaállítás / Előzmények / SQL fülön (H1)
- A `RestoreViewModel`, a `HistoryViewModel` és a `SqlViewModel` iratkozzon fel a `MainViewModel.ConfigReloaded`-re, és küldjön `OnPropertyChanged`-et a számított listákra (`JobIds` stb.), mentés után is.
- A combók kapjanak `MinWidth`-t.
- Teszt (hamis IPC-kliens): `RefreshAsync` után a `JobIds` tartalmazza a feladatokat, és a `PropertyChanged` kiváltódott.

### B2. Új feladat varázsló (a SPEC 11.2 2. pontjának megvalósítása)

**Cél.** Cobian / Veeam-szerű menet: **Új feladat → Tovább → … → Befejezés**. Emberi szövegek, legördülők a meglévő célokból és tárolókból, lépésenkénti ellenőrzés, a végén összegzés és próbafuttatás. **Szerkesztés ugyanezzel a varázslóval.** A haladó mezők külön „Speciális” lapon vannak.

**Felépítés.**
- `BackupForge.Gui.Core`: `JobWizardViewModel`, amely a lépések listáját és a lépések ViewModel-jeit tartja; `CanGoNext` a lépés saját ellenőrzéséből; Vissza / Tovább / Befejezés / Mégse.
- `BackupForge.Gui`: `JobWizardWindow` (modális), lépésenként `DataTemplate`, bal oldalt a lépések listája; a kész lépésre vissza lehet kattintani.
- Minden logika a Gui.Core-ban, Linuxon tesztelhetően.
- A varázsló egy **piszkozat-feladaton** dolgozik (`JobConfig` másolat). A meglévő konfigot csak a Befejezés módosítja, és csak a szolgáltatás validálása után (`config.save`). Mégse → nincs változás.

**Közös lépések minden típusnál:**
1. **Típus** (csak új feladatnál; szerkesztéskor nem változtatható). Három nagy választógomb, egy-egy mondattal:
   - Fájlmentés: „pontos tükör vagy frissítés egy mappába, régi változatokkal”;
   - Tárolós mentés: „restic: tömörített, titkosított, deduplikált pillanatképek”;
   - SQL Server-mentés: „adatbázisok mentése és ellenőrzése”.
2. …típusonkénti lépések (lent)…
3. **Ütemezés.** Választógombok:
   - óránként / N óránként (kezdés, opcionális időablak);
   - naponta (egy vagy több időpont, a hét napjai pipákkal);
   - hetente;
   - havonta (nap vagy „hónap utolsó napja”);
   - **egy másik feladat után** (legördülő: „a(z) X sikeres futása után”, önmaga és a kört okozó feladatok nélkül);
   - csak kézzel.

   Alatta élő előnézet emberi nyelven (a meglévő `ScheduleText`), és **„A következő 5 futás”** listája (`ScheduleCalculator`). A várható gyakoriság (`expected_interval_h`) számolt értéke is látszik, felülírni a Speciális lapon lehet.
4. **Név.**
   - Megnevezés: alapértelmezés a forrásból és a célból, pl. „Company → NAS1”.
   - Azonosító: a névből képzett, a szabálynak megfelelő slug; a Speciális lapon szerkeszthető. Létrehozás után csak figyelmeztetéssel módosítható, mert az előzmények hozzá kötődnek.
   - Engedélyezve pipa.
5. **Összegzés.**
   - A feladat egy-két emberi mondatban, pl. „Naponta 22:30-kor a D:\Company mappát pontos tükörként menti a NAS1 (\\server\backup) Company mappájába. A törölt és felülírt fájlokat 21 napig a Company_Versions mappában őrzi.”
   - A szolgáltatás validálásának eredménye: hibák (piros, a Befejezés tiltva) és figyelmeztetések (sárga), kattintásra a megfelelő lépésre ugrik.
   - Gombok: **Próbafuttatás** (mentés nélkül, lásd B4), **Befejezés**, **Befejezés és futtatás most**.

**Fájlmentés-lépések:**
- **Forrás.**
  - Mappák listája (Hozzáadás… tallózó, Eltávolítás).
  - Forrásonként „Kizárások”: az alapértelmezett kizárások is látszanak, mintánként egy sor, rövid súgóval (`*.tmp`, `~$*`, `\$RECYCLE.BIN\`).
  - Minden forrást a **szolgáltatás nevében** ellenőriz (B4 `path.check`). A szolgáltatás LocalSystem-ként a felhasználó hálózati meghajtóbetűit (`Z:`) nem látja. Ha a forrás hálózati meghajtóbetű, figyelmeztessen, és ajánlja fel az UNC-utat.
- **Cél.**
  - Legördülő a meglévő célokból: név, útvonal, szabad hely, elérhetőség az Áttekintés adataiból.
  - **„Új cél…”** gomb: kis párbeszédablak (helyi mappa / hálózati megosztás + felhasználó + jelszó), „Kapcsolat tesztelése” a szolgáltatás nevében. Sikertelen tesztnél a mentéshez külön megerősítés kell. Az új cél a varázsló Befejezésével együtt mentődik.
  - Almappa: alapértelmezés a (első) forrásmappa neve.
  - Élőben kiírva a **tükör** és a **Versions** mappa végső útvonala.
  - **Átfedés-ellenőrzés itt, azonnal** (a piszkozattal kiegészített konfig `config.validate`-je, késleltetve): a CR-001 `path.overlap_job` hibája ennél a lépésnél jelenjen meg, emberi szöveggel.
- **Mód és megőrzés.**
  - Választógombok:
    - „Pontos tükör — a forrásból törölt fájl a célból is törlődik (a Versions mappába kerül)”;
    - „Csak frissítés — a célból soha nem töröl”.
  - „Régi változatok megőrzése [21] napig” (pipa + szám).
  - A törlési biztosíték egy mondatban: „Ha egy futás a fájlok több mint [20] %-át (legalább [50] fájlt) törölné, a futás megáll, és jóváhagyásra vár.”
  - VSS: „Nyitott fájlok mentése pillanatképpel (VSS)” — ajánlott / kötelező / soha.

**Tárolós mentés (restic) lépései:**
- **Forrás.**
  - Mappák (mint fent), **vagy** „Egy SQL-feladat mentései”: legördülő az SQL-feladatokból → `@staging:<id>`. Ilyenkor az ütemezés lépés alapértelmezése „az SQL-feladat után”.
- **Tároló.**
  - Legördülő a meglévő tárolókból, **„Új tároló…”** gombbal (a meglévő tároló-varázsló: generált jelszó, helyreállítási lap, init csak megerősítéssel).
  - A tároló megőrzési szabálya csak olvasható összefoglalóként látszik („utolsó 3, 14 napi, 8 heti, 12 havi, 2 éves”), a módosítás a Célok és tárolók fülön történik.
  - Ha a tároló nincs inicializálva: **„Tároló létrehozása most…”** gomb (G4), CLI-parancs említése nélkül.

**SQL Server-mentés lépései:**
- **Példány.**
  - `sql.discover` alapján legördülő (példány, verzió, kiadás); kézi megadás is lehet.
  - Hitelesítés: Windows (ajánlott, a szolgáltatás fiókjával) / SQL-login (felhasználó + jelszó → titok).
  - **Jogosultság-ellenőrzés:** ha a szolgáltatásnak nincs joga, sárga sáv és **„Jogosultságok beállítása…”** gomb (a meglévő funkció, a T-SQL előbb megjelenik).
- **Adatbázisok.**
  - „Minden felhasználói adatbázis (az újak is automatikusan)” — ajánlott —, vagy „Kiválasztott adatbázisok”: pipás lista a felderítésből (név, recovery model, méret, figyelmeztetés). Mellette: „Rendszer-adatbázisok is”.
  - A FULL recovery figyelmeztetés itt is látszik.
- **Beállítások.**
  - Helyi példányok száma (`keep_local`), „Ellenőrzés a mentés után (RESTORE VERIFYONLY)”, CHECKDB ütemezése (hetente / soha).
  - **„A mentéseket utána tárolóba is küldöm: [tároló ▼]”** pipa. Bekapcsolva a Befejezés **két feladatot** hoz létre: az SQL-feladatot és egy `<id>-restic` láncolt restic-feladatot (`@staging:<id>`, ütemezés: „az SQL-feladat után”). Ez az ajánlott SQL → restic lánc, egy menetben.

**Speciális lap** (a varázsló bármelyik lépéséről elérhető, és szerkesztésnél ez a teljes mezőlista):
- a mostani mezős szerkesztő összes mezője, **emberi címkékkel és rövid súgóval** (G5): összehasonlítás módja, VSS, a biztonsági küszöbök, `expected_interval_h`, catch-up, restic-címkék, `copy_only`, tömörítés, Versions-mappa helye, max. verzió fájlonként, azonosító;
- a nyers kulcsnév legfeljebb halvány súgóban szerepelhet (pl. „`delete_guard_pct`”), címkeként nem.

**Feladatok fül:**
- Táblázat: név, típus, ütemezés szövegesen, cél / tároló, utolsó eredmény.
- Gombok: Új feladat…, Szerkesztés…, Másolás…, Letiltás / Engedélyezés, Törlés. A törlés megerősítéssel jár; az előzmények maradnak, a célon lévő adatok nem törlődnek, és ezt a megerősítő szöveg ki is mondja.
- A régi mezős szerkesztő megszűnik (a Speciális lap váltja ki).

**Tesztek (Gui.Tests, hamis IPC-klienssel):**
- mindhárom típus végigvitele → a várt `JobConfig`;
- SQL + „tárolóba is” → két feladat, helyes lánccal;
- **szerkesztés oda-vissza veszteségmentes**: meglévő feladat → varázsló → Befejezés változtatás nélkül → bájtra azonos JSON (a mostani oda-vissza teszt mintájára, minden típusra, minden mezővel);
- a lépésenkénti ellenőrzés tiltja a Továbbot (üres forrás, nincs cél, rossz időpont);
- az átfedési hiba a Cél lépésnél jelenik meg;
- a „másik feladat után” listából hiányzik önmaga és a kört okozó feladat;
- az azonosító-slug képzése (ékezetek, szóközök, ütközés → `-2`);
- az ütemezés-előnézet és a következő 5 futás;
- Mégse → a konfig változatlan.

### B3. Célok és tárolók fül (G1, G3, G4)
- A cél szerkesztője típus-választógombbal: helyi mappa / hálózati megosztás. A felhasználó és a jelszó csak hálózatinál látszik.
- A mezők mintaszövege (`\\server\share\mappa`) **valódi vízjel** (szürke, gépeléskor eltűnik), ne kitöltött érték (G3).
- „Kapcsolat tesztelése” a szolgáltatás nevében (ma is így van). Az eredmény: elérhető, írható, szabad hely, vagy a hiba emberi szövege.
- Restic-tárolónál a „nincs inicializálva” állapot jelvényként látszik, mellette „Létrehozás…” (G4).

### B4. Új / bővített IPC-műveletek
- `path.check` (`{path}` → `{exists, listable, is_network_drive_letter, error}`) a szolgáltatás nevében, LocalSystem-ként.
- `job.dryrun` **piszkozattal**: a meglévő `job.dryrun` (mentett feladat) mellett `{job: <JobConfig>, targets?, repos?}` is elfogadható. A szolgáltatás a piszkozatot a futó konfiggal együtt validálja, és **mentés nélkül** csak tervez:
  - fájlnál terv a futásnaplóba és a válaszba (összegzés + az első N tétel);
  - resticnél `restic backup --dry-run`;
  - SQL-nél felderítés + jogosultság- és szabadhely-ellenőrzés.

  A meglévő dry-run szabályok érvényesek (nem indít láncot, nem ír a célra).
- A protokollverzió nőjön. Eltérésnél a GUI érthető üzenetet ad („a szolgáltatás régebbi — futtasd újra a telepítőt”).

### B5. Emberi hibaüzenetek (G2)
- A `ValidationError(path, code, args)` → GUI-szöveg: „<feladat neve>: <mező címkéje>: <üzenet>”, pl. „Company → NAS1: Cél: nincs kiválasztva”. A belső út (`jobs[0].target.target_id`) nem jelenik meg.
- Kattintásra a hibához tartozó varázsló-lépés vagy mező nyílik meg.
- A leképezés egy táblázat a Gui.Core-ban (útvonal-minta → lépés + mező-címkekulcs), teszttel, ami **a validátor minden hibakódjára és mezőjére** ellenőrzi, hogy van leképezés (új validátor-szabály ne maradhasson ki észrevétlenül).

### B6. Szövegek
Minden új szöveg a `Resources/UiStrings(.hu).resx`-be kerül (hu + en). A magyar a fő nyelv: rövid, köznyelvi mondatok, a „Tovább” / „Vissza” / „Befejezés” / „Mégse” gombnevekkel.

---

## Nem része a CR-nek
- F8 (BackupPulse-integráció) — külön CR a BackupPulse repóban.
- E-mail-kimenő sor perzisztálása, `versions.size_gb` számítása (a `CLAUDE.md` „Ismert teendők” listáján marad).

## Kézi tesztek frissítése (`docs/MANUAL-TESTS.md`)
- C pont átírása a varázslóra:
  - új fájlfeladat új SMB-céllal, kapcsolatteszttel;
  - SQL-feladat „tárolóba is” lánccal;
  - szerkesztés;
  - Speciális lap;
  - hibás piszkozat → emberi hiba, kattintásra a lépésre ugrik;
  - Mégse.
- Új pontok: VSS writerekkel valós szerveren (A1); SMB próba + futás versenyhelyzet 30 percig (A2); leállítás nagy fájl közben < 5 mp (A3).

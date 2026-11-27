# Kézi teszt — észrevételek (gyűjtő)

> A `docs/MANUAL-TESTS.md` végigvitele közben talált hibák és észrevételek. A chat ebből ír CR-t (a hibákból egy javító CR-t, a GUI-észrevételek a CR-002 GUI-varázslóba kerülnek). Új tétel a lista végére; ha egy tétel CR-be került, a CR számát kapja.

Teszt-környezetek:
- **T1** — Windows 11 munkaállomás (Z790), BackupForge 0.1.0 (`main` @ `d3897fb` CI-telepítője), helyi cél + helyi restic-tároló, GUI, 2026-11-25.
- **T2** — tiszta **Windows Server 2022 Standard Evaluation** VM (Proxmox, 4 vCPU / 6 GB, csak C:), BackupForge 0.1.0 (`main` @ `8fe8620` CI-telepítője, `BackupForge-Setup-0.1.0.26.exe`), SQL Server 2025 Express (`.\SQLEXPRESS`, 17.0), helyi cél + helyi SMB-megosztás külön helyi felhasználóval (`\\localhost\nas`) + helyi restic-tároló; minden CLI-ből (`bf.exe`) és PowerShellből, SSH-n át, 2026-11-25 22:28–23:05. GUI-t itt nem teszteltünk.

## Hibák

### H1 — A Visszaállítás fül „Feladat” listája üres (2026-11-25) — a CR-002 GUI-körrel együtt javítandó (user-döntés) → CR-002 — **→ CR-002 kész (B1)**
- **Lépés**: két feladat elmentve (`files-1` fájlmentés, `restic-1` restic), mindkettő lefutott `ok`-kal → Visszaállítás fül.
- **Elvárt**: a „Feladat” legördülőben `files-1` és `restic-1`.
- **Tényleges**: a lista üres, így visszaállítási pont sem választható; a fül használhatatlan.
- **Ok (kódból)**: `RestoreViewModel.JobIds` (`src/BackupForge.Gui.Core/ViewModels/OtherViewModels.cs`) számított `IEnumerable` property, értesítés nélkül; a `RestoreViewModel` nem iratkozik fel a `MainViewModel.ConfigReloaded` eseményre (a Jobs / Targets / Notifications / Settings igen). A WPF a kötéskor (még üres konfiggal) egyszer olvassa ki, utána soha. **Ugyanez a minta**: `HistoryViewModel.JobIds` (szűrő), `SqlViewModel.SqlJobIds` (csak a Felderítés után frissül).
- **Javítás**: a három ViewModel iratkozzon fel a `ConfigReloaded`-re, és hívjon `OnPropertyChanged(nameof(JobIds))`-t (mentés után is — a mentés `RefreshAsync`-et hív); a combóknak `MinWidth` (most összecsukott, üresnek látszik). Teszt: a meglévő hamis IPC-kliensű Gui-tesztek mintájára — `RefreshAsync` után a `JobIds` tartalmazza a feladatokat, és a `PropertyChanged` kiváltódik.
- **Kerülőút addig**: `bf restore <jobId> --list` / `--browse` / `--from … --path … --to …` (rendszergazdai PowerShell).

### H2 — A VSS-pillanatkép valós szerveren sem készül el: „snapshot has no device object” (T2, 2026-11-25) — FONTOS → CR-002 — **→ CR-002 kész (A1; ellenőrzésre vár (chat): `bf diag vss` a tesztgépen)**
- **Lépés**: `vss: when_available` fájlfeladat (`docs-local`, C:\ forrás) futtatása.
- **Tényleges**: minden futás `warning` „a VSS-pillanatkép nem készült el (snapshot has no device object) — VSS nélkül mentve”. A writerek mind `Stable / No error` (`vssadmin list writers`), és `vssadmin create shadow /for=C:` ugyanezen a gépen hibátlanul működik. A BackupForge tehát **gyakorlatilag soha nem használ VSS-t** — zárolt fájlok (PST, nyitott adatbázisfájl) mentése így nem működik. A tartalék helyesen `warning`-ot ad (a CR-001 2. pontja működik), és árva pillanatkép nem maradt.
- **Gyanú (kódból)**: `src/BackupForge.Windows/Vss.cs` 113–115. sor: a `GetSnapshotProperties` után a `VssSnapshotProp.SnapshotDeviceObject` `IntPtr` üres. Valószínűleg a `IVssBackupComponents` vtable-sorrendje csúszik el (egy másik metódus hívódik meg S_OK-val), vagy a `VSS_SNAPSHOT_PROP` marshalingja hibás. Elképzelhető, hogy a CI-runner „writerek nem válaszolnak” jelensége is ugyanez a hiba.
- **Javítás**: vtable-sorrend ellenőrzése a `vsbackup.h` alapján (a `GetSnapshotProperties` pontos indexe), a struktúra-elrendezés ellenőrzése, és egy Windows-integrációs teszt, ami **megköveteli**, hogy egy egészséges gépen a pillanatkép elkészüljön (eszközútvonal nem üres, egy zárolt fájl a pillanatképből olvasható). A CI-n, ha a runner writerei tényleg nem válaszolnak, a writerek nélküli ágat külön teszt fedje.

### H3 — SMB-cél: időnként 1219-es hiba („már van kapcsolat más hitelesítéssel”) (T2, 2026-11-25) — FONTOS → CR-002 — **→ CR-002 kész (A2; ellenőrzésre vár (chat): 30 perces SMB-forgatókönyv)**
- **Lépés**: SMB-cél saját felhasználóval (`\\localhost\nas`, `BF-TEST\bfnas`), az első futás `ok`; néhány perc múlva újabb futás.
- **Tényleges**: a futás `failed` „a cél nem elérhető: … (error 1219)”, **ugyanabban a másodpercben**, amikor a `TargetProbe` (5 percenkénti célellenőrzés) is 1219-et kapott (`22:33:40`). Később, külön időpontban a futás ismét `ok`. A futás után a kapcsolat megmarad (`Get-SmbConnection`: `localhost\nas`, credential `bfnas`) — a SPEC 14. szerint a futás végén `WNetCancelConnection2` kellene.
- **Gyanú**: a cél-ellenőrzés és a futás egyszerre hív `WNetAddConnection2`/`WNetCancelConnection2`-t ugyanarra a szerverre; az egyik bontja vagy átmenetileg más állapotban hagyja a kapcsolatot, a másik 1219-et kap.
- **Javítás**: egyetlen, szerverenként zárolt, referenciaszámlált kapcsolatkezelő (a próba és a futás ugyanazt használja); ha már van kapcsolat **ugyanazzal a felhasználóval**, azt használja újra (nem 1219); bontás csak az utolsó használó után. Teszt: párhuzamos próba + futás ugyanarra a szerverre.

### H4 — Restic-visszaállítás nem létező útvonalra „ok, 0 fájl” (T2) — közepes → CR-002 — **→ CR-002 kész (A4)**
- `bf restore docs-restic --from <snap> --path "C:/data/docs/file1.txt"` → `ok`, „0 fájl visszaállítva”. A helyes forma a snapshoton belül `/C/data/docs/file1.txt` (azzal működik). Ha a megadott útvonal nem létezik a snapshotban, a visszaállítás legyen `failed` („nincs ilyen útvonal a pillanatképben”), és a CLI fogadja el a Windows-formát is (`C:\data\…` → `/C/data/…`). Apróság: egy fájl visszaállítása „4 fájl”-t ír (a mappákat is számolja) — csak a fájlokat számolja.

### H5 — Két visszaállítás ugyanabba a célmappába ugyanabban a másodpercben ütközik (T2) — kicsi → CR-002 — **→ CR-002 kész (A5)**
- A `Restore_<yyyy-MM-dd HHmmss>` név másodperc-pontosságú; két egymás utáni `bf restore … --to C:\restore-test` ugyanabban a másodpercben → a második `failed` „restore target is not empty”. Ütközéskor kapjon utótagot (`… (2)`).

### H6 — Szolgáltatás leállítása nagy fájl másolása közben ~70 mp (T2) — kicsi → CR-002 — **→ CR-002 kész (A3; ellenőrzésre vár (chat))**
- 3 GB-os (768 MB-os fájlokból álló) másolás közben `Restart-Service` → a futás helyesen `interrupted`, a `.bfpart` eltűnt, árva VSS nincs, a következő futás befejezi — **de** a leállás kivárta a teljes 60 mp-es türelmi időt („1 run(s) did not stop within 00:01:00”). A másolás fájlon belül is figyelje a megszakítást (darabos másolás `CancellationToken`-nel), hogy a leállás néhány mp legyen.

### H7 — `secrets.dat` ACL-jében a telepítő felhasználó is szerepel (T2) — kicsi → CR-002 — **→ CR-002 kész (A6)**
- `icacls secrets.dat` → `NT AUTHORITY\SYSTEM:(F)` **és** `BF-TEST\Administrator:(F)` (a csendes telepítést futtató admin). A SPEC 14. szerint csak SYSTEM. Ok: a `DpapiSecretStore` a futtató felhasználót is hozzáadja, ha nem SYSTEM — a telepítő-segéd így adja meg. Telepítéskor (`--setup-install`) az ACL legyen csak SYSTEM.

### H8 — Kódolás (T2) — kicsi → CR-002 — **→ CR-002 kész (A7)**
- `bf.exe` kimenete átirányítva (SSH, `| Out-File`, ütemezett feladat) ékezethibás (`m�solva`), mert a konzol kódlapján ír; `[Console]::OutputEncoding = UTF8` mellett helyes. A `bf` állítsa UTF-8-ra a kimenetét, ha átirányított.
- `setup.log` UTF-8 BOM nélkül, PS 5.1 `Get-Content`-tel a „—” jel `�?"`-ként jelenik meg. A setup.log (és a szolgáltatásnapló) kapjon BOM-ot, vagy csak ASCII-t írjon.

## GUI-észrevételek (→ CR-002 B rész)
- G1: a feladatnál a cél (`targetid`) és a restic-tároló (`repoid`) kézzel beírt azonosító — legördülő lista kell a meglévőkből + „Új…”. **→ CR-002 kész (B2/B3)**
- G2: mentési hibaüzenet belső útvonallal (`jobs[0].target.target_id: Kötelező mező`) — emberi szöveg kell („files 1 feladat: nincs kiválasztva cél”), kattintásra a mezőhöz ugrás. **→ CR-002 kész (B5)**
- G3: az új SMB-cél útvonalmezőjében a mintaszöveg (`\\server\share\Z790`) kitöltött értéknek látszik. **→ CR-002 kész (B3)**
- G4: a restic próbafuttatás „a tároló nem létezik … `bf repo init repo1`” üzenete mellé „Tároló létrehozása most…” gomb (a meglévő új-tároló varázslót indítja); GUI-ban ne CLI-parancsra hivatkozzunk. **→ CR-002 kész (B2/B3)**
- G5: a mezőcímkék nyers kulcsnevek (`kind`, `everymin`, `dayof_month`, `onsuccess`, `deleteguard_pct`, …). **→ CR-002 kész (B2 Speciális lap)**

## Egyéb
- E1 (→ CR-002 A8): a telepítő > 100 MB: a CI három self-contained single-file exe-t publikál (szolgáltatás, `bf`, GUI), mindegyikben a teljes .NET runtime (a GUI-ban a WPF is). Javaslat: egy közös programmappa, self-contained, de nem single-file (a runtime egyszer). Nem sürgős. **→ CR-002 kész (A8: 114,3 → 57,5 MB)**

## Rendben (T2 — Windows Server 2022, tiszta VM)
- **A — telepítés**: csendes telepítés `/CONFIG` + `/SECRETS`-szel (exit 0; a setup.log figyelmeztet a secrets-fájl törlésére), szolgáltatás `Running`, LocalSystem, `SERVICE_SID_TYPE: UNRESTRICTED`, helyreállítás 3× restart 60 s / reset 86400; ProgramData ACL öröklés nélkül SYSTEM + Administrators; eseménynapló 1000. **Frissítés** ugyanazzal a telepítővel: konfig (hash) és előzmények változatlanok, a titkok működnek. **Eltávolítás** megtartással: szolgáltatás, programmappa, eseményforrás eltűnik, `config.json` + `history.db` marad; újratelepítés után ugyanazok a feladatok. **`/PURGE`**: az adatmappa is törlődik. A mentési célok (helyi, SMB, restic) **mindhárom esetben bájtra azonos fájlszámmal megmaradtak**; árva VSS nincs.
- **B — futások**: tükör + Versions (módosított és törölt fájl a `docs_Versions\<időbélyeg>`-be, a tükörben az új tartalom), Readonly attribútum megmarad, 274 karakteres útvonal átmásolva; **B4 törlési biztosíték**: 150/252 fájl (59,5 %) → `failed`, semmi nem törlődött, 2003-as esemény; `bf approve-delete` után a következő futás a Versions-be mozgat; **B3**: hiányzó forrás → `failed` „a forrás nem elérhető”, üres forrás → `failed` „üres forrást soha nem tükrözünk”, a tükör érintetlen; **B1** szolgáltatás-újraindítás 3 GB másolása közben → `interrupted`, `.bfpart` nem maradt, a következő futás befejezte (lásd H6); **B2 catch-up**: a szolgáltatás leállítva az esedékesség alatt → indulás után 2 perccel `catch_up` futás; **B5** az SMB-megosztás megszüntetése másolás közben → `failed` „a cél nem elérhető” (~35 s), a megosztás visszaállítása után a következő futás `ok`, `.bfpart` nem maradt; **B6** SMB-cél saját felhasználóval meghajtóbetű nélkül, `bf target test` (lásd H3).
- **restic**: init csak `bf repo init --yes`-szel (előtte a futás `failed`, magától nem hoz létre tárolót), backup, snapshots, visszaállítás új `Restore_<időbélyeg>` mappába (lásd H4).
- **Visszaállítás fájlmentésből** (`bf restore`): pontok listája (aktuális + Versions-időbélyegek), egy fájl régi változata visszaállítva; eredeti helyre megerősítés nélkül elutasítva.
- **C.8 átfedés** (`bf config validate`): a célgyökérbe mentő tükör → 3 érthető, magyar hiba (tükör ↔ tükör, tükör ↔ Versions).
- **E — SQL (SQL Server 2025 Express)**: felderítés (példány, verzió, recovery model, méret), BACKUP + VERIFYONLY, tömörítés nélkül restic-lánc miatt, FULL recovery figyelmeztetés (`erp`), **SQL → restic lánc** (`chain` trigger, ugyanaz a lánc), `keep_local = 2` (adatbázisonként 2 `.bak` maradt), `bf sql grant` (a T-SQL megjelenik, 6 adatbázis). Megfigyelés: a grant a `model`-re is kiadja a `db_backupoperator`-t, ezért **az utána létrehozott új adatbázis is azonnal menthető** (jó mellékhatás — a SPEC 8.6 „új db → nincs jogosultság” esete így ritkább). Megjegyzés: ezen a telepítésen eleinte a SYSTEM és a BUILTIN\Administrators is sysadmin volt (a telepítő SYSTEM-ként futott), ezért a „jog nélkül” esetet ezek elvétele után teszteltük.
- **Titkok**: konfig-exportban nincs jelszó; a naplókban és a `status.json`-ban a jelszó nem szerepel (kis- és nagybetű-érzékeny kereséssel ellenőrizve).
- **Heti riport**: `bf report preview` HTML-t ír (3,6 KB). E-mail-küldést (D pont) itt nem teszteltünk (nincs SMTP).

## Nem tesztelt (T2)
- GUI (C pont, kivéve C.8 CLI-ből), e-mail (D), CHECKDB-ütemezés (E2), betelt staging-kötet (E3), gép-újraindítás (B2 csak szolgáltatás-leállítással), a késleltetett automatikus indítás (`sc qc` sor a kimenetből kimaradt).

## Rendben (T1, eddig)
- Telepítés, szolgáltatás indul; fájlmentés helyi célra `ok`; restic-tároló létrehozása a GUI varázslóval, restic-mentés `ok` (4257 fájl, +125,3 MB); próbafuttatás; a nem létező restic-tároló nem jön létre magától (szándékos); a mentés nélküli feladat validálása (hiányzó cél) megfogja a hibát; a célok szabad helye az Áttekintésen.

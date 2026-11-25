# Kézi teszt — észrevételek (gyűjtő)

> A `docs/MANUAL-TESTS.md` végigvitele közben talált hibák és észrevételek. A chat ebből ír CR-t (a hibákból egy javító CR-t, a GUI-észrevételek a CR-002 GUI-varázslóba kerülnek). Új tétel a lista végére; ha egy tétel CR-be került, a CR számát kapja.

Teszt-környezet: Windows 11 munkaállomás (Z790), BackupForge 0.1.0 (`main` @ `d3897fb` CI-telepítője), helyi cél + helyi restic-tároló, 2026-11-25.

## Hibák

### H1 — A Visszaállítás fül „Feladat” listája üres (2026-11-25) — a CR-002 GUI-körrel együtt javítandó (user-döntés)
- **Lépés**: két feladat elmentve (`files-1` fájlmentés, `restic-1` restic), mindkettő lefutott `ok`-kal → Visszaállítás fül.
- **Elvárt**: a „Feladat” legördülőben `files-1` és `restic-1`.
- **Tényleges**: a lista üres, így visszaállítási pont sem választható; a fül használhatatlan.
- **Ok (kódból)**: `RestoreViewModel.JobIds` (`src/BackupForge.Gui.Core/ViewModels/OtherViewModels.cs`) számított `IEnumerable` property, értesítés nélkül; a `RestoreViewModel` nem iratkozik fel a `MainViewModel.ConfigReloaded` eseményre (a Jobs / Targets / Notifications / Settings igen). A WPF a kötéskor (még üres konfiggal) egyszer olvassa ki, utána soha. **Ugyanez a minta**: `HistoryViewModel.JobIds` (szűrő), `SqlViewModel.SqlJobIds` (csak a Felderítés után frissül).
- **Javítás**: a három ViewModel iratkozzon fel a `ConfigReloaded`-re, és hívjon `OnPropertyChanged(nameof(JobIds))`-t (mentés után is — a mentés `RefreshAsync`-et hív); a combóknak `MinWidth` (most összecsukott, üresnek látszik). Teszt: a meglévő hamis IPC-kliensű Gui-tesztek mintájára — `RefreshAsync` után a `JobIds` tartalmazza a feladatokat, és a `PropertyChanged` kiváltódik.
- **Kerülőút addig**: `bf restore <jobId> --list` / `--browse` / `--from … --path … --to …` (rendszergazdai PowerShell).

## GUI-észrevételek (→ CR-002, varázsló)
- G1: a feladatnál a cél (`targetid`) és a restic-tároló (`repoid`) kézzel beírt azonosító — legördülő lista kell a meglévőkből + „Új…”.
- G2: mentési hibaüzenet belső útvonallal (`jobs[0].target.target_id: Kötelező mező`) — emberi szöveg kell („files 1 feladat: nincs kiválasztva cél”), kattintásra a mezőhöz ugrás.
- G3: az új SMB-cél útvonalmezőjében a mintaszöveg (`\\server\share\Z790`) kitöltött értéknek látszik.
- G4: a restic próbafuttatás „a tároló nem létezik … `bf repo init repo1`” üzenete mellé „Tároló létrehozása most…” gomb (a meglévő új-tároló varázslót indítja); GUI-ban ne CLI-parancsra hivatkozzunk.
- G5: a mezőcímkék nyers kulcsnevek (`kind`, `everymin`, `dayof_month`, `onsuccess`, `deleteguard_pct`, …).

## Egyéb
- E1: a telepítő > 100 MB: a CI három self-contained single-file exe-t publikál (szolgáltatás, `bf`, GUI), mindegyikben a teljes .NET runtime (a GUI-ban a WPF is). Javaslat: egy közös programmappa, self-contained, de nem single-file (a runtime egyszer). Nem sürgős.

## Rendben (eddig)
- Telepítés, szolgáltatás indul; fájlmentés helyi célra `ok`; restic-tároló létrehozása a GUI varázslóval, restic-mentés `ok` (4257 fájl, +125,3 MB); próbafuttatás; a nem létező restic-tároló nem jön létre magától (szándékos); a mentés nélküli feladat validálása (hiányzó cél) megfogja a hibát; a célok szabad helye az Áttekintésen.

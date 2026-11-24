# BackupForge

Windows-szolgáltatásként futó mentőalkalmazás kis- és középvállalati szerverekre:

- **Fájlmentés** tükör / frissítés módban, `Versions` mappákkal és megőrzési idővel (FreeFileSync-szerű), VSS-sel, törlési biztosítékkal
- **restic-tároló**: verziózott, deduplikált, titkosított mentés helyi lemezre, NAS-ra, SFTP-re, S3-ra vagy rest-serverre
- **SQL Server-mentés** ellenőrzéssel, láncban a fájl- vagy restic-mentéssel
- Párhuzamos feladatok, láncok, elmaradt futások pótlása
- E-mail-riasztás, heti riport, Windows-eseménynapló, `status.json` külső figyelőknek (pl. BackupPulse)
- Egyetlen telepítő, grafikus konfigurátor, `bf.exe` parancssor

> Állapot: **fejlesztés (v1, F0–F7 elkészült, pilot előtt)** — a specifikáció a `docs/SPEC.md`-ben, a fejlesztési állapot a `CLAUDE.md`-ben.

## Fejlesztés
```bash
dotnet build BackupForge.slnx      # .NET 10 SDK (global.json), warnings as errors
dotnet test BackupForge.slnx       # Linuxon a platformfüggetlen rész, Windowson minden
python3 tools/fetch_restic.py      # a rögzített restic.exe letöltése + ellenőrzése (Windows)
```
- Szerkezet: `src/` (Core, Engine.Files / Restic / Sql, Windows, Ipc, Service, Cli = `bf.exe`, Gui.Core + Gui = WPF), `tests/`, `installer/` (Inno Setup), `schema/` (`config.schema.json`, `status.schema.json`).
- CLI: `bf --help`. Eseményazonosítók: [docs/EVENT-IDS.md](docs/EVENT-IDS.md). SQL-jogok: [docs/SQL-PERMISSIONS.md](docs/SQL-PERMISSIONS.md). Kézi tesztek: [docs/MANUAL-TESTS.md](docs/MANUAL-TESTS.md).

## Dokumentáció
- [Specifikáció](docs/SPEC.md)
- [Státusz-szerződés (status.json)](docs/STATUS-CONTRACT.md)
- [Döntésnapló](docs/DECISIONS.md)

## Licenc
MIT ([LICENSE](LICENSE)).

## Harmadik fél
A restic-mentéshez a [restic](https://restic.net) változatlan, hivatalos binárisát használja (BSD 2-Clause licenc).

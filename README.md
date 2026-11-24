# BackupForge

Windows-szolgáltatásként futó mentőalkalmazás kis- és középvállalati szerverekre:

- **Fájlmentés** tükör / frissítés módban, `Versions` mappákkal és megőrzési idővel (FreeFileSync-szerű), VSS-sel, törlési biztosítékkal
- **restic-tároló**: verziózott, deduplikált, titkosított mentés helyi lemezre, NAS-ra, SFTP-re, S3-ra vagy rest-serverre
- **SQL Server-mentés** ellenőrzéssel, láncban a fájl- vagy restic-mentéssel
- Párhuzamos feladatok, láncok, elmaradt futások pótlása
- E-mail-riasztás, heti riport, Windows-eseménynapló, `status.json` külső figyelőknek (pl. BackupPulse)
- Egyetlen telepítő, grafikus konfigurátor, `bf.exe` parancssor

> Állapot: **tervezés** — a specifikáció a `docs/SPEC.md`-ben, a fejlesztési állapot a `CLAUDE.md`-ben.

## Dokumentáció
- [Specifikáció](docs/SPEC.md)
- [Státusz-szerződés (status.json)](docs/STATUS-CONTRACT.md)
- [Döntésnapló](docs/DECISIONS.md)

## Harmadik fél
A restic-mentéshez a [restic](https://restic.net) változatlan, hivatalos binárisát használja (BSD 2-Clause licenc).

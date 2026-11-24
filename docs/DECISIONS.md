# Döntésnapló

Formátum: dátum — döntés — indoklás. Új döntés a lista végére; visszavont döntést nem törlünk, hanem „VISSZAVONVA (dátum): …” megjegyzést kap.

## 2026-11-24 — tervezési beszélgetés (chat)

1. **Saját mentőapp, Windows-szolgáltatásként, egyetlen telepítővel.** Tudásban a Cobian Reflector / FreeFileSync kategória + SQL-mentés + felügyelet. — A mostani megoldások (FFS + .bat + PowerShell értesítő, Cobian, Macrium Free) csendben elromolhatnak (pl. egy kikommentezett sor miatt hónapokig nem ment, miközben a napló rendben lévőnek látszik), a Feladatütemező / SYSTEM / meghajtóbetű kombináció törékeny, a Macrium Free megszűnt.
2. **Nem forkoljuk és nem csomagoljuk be a FreeFileSync-et.** — A forrás GPLv3, de a szerző kiegészítése szerint a standard és a Donation Edition csak magáncélra használható, céges használathoz Business licenc kell (eszközönként), és a harmadik fél telepítőjébe építés tiltott. C++/wxWidgets kódbázis, idegen karbantartás.
3. **Kétféle mentés, párhuzamosan futtatható feladatokként:**
   - **FFS-szerű fájlszintű mentés Versions mappákkal — saját C# motor.** A robocopy `/MIR` nem tud fájlonként verziózni, ezért a döntést fájlonként nekünk kell meghozni. Az ügyfél böngészhető mappát lát a NAS-on.
   - **restic-tároló** (verziózott, deduplikált, titkosított) — a hivatalos `restic.exe` becsomagolva, parancssorból vezérelve `--json`-nal. Nem fork, nem könyvtárként (Go; a restic belső csomagjai nem importálhatók).
4. **restic, nem Kopia.** — A mi szolgáltatásunk az „agy” (ütemezés, megőrzés, riasztás); ehhez egyszerű, konzervatív motor kell. A restic Windowson beépítve tud VSS-t (`--use-fs-snapshot`), stabil a tárolóformátuma, nagy a felhasználói bázisa, és nincs saját házirend-rendszere, ami a miénkkel átfedne. A Kopia gyorsabb, de a házirend/ütemező/UI-ja duplikáció lenne. Licenc: restic BSD-2 → becsomagolható.
5. **SQL-mentés kialakítását a user Claude-ra bízta** — a SPEC 8. pontja szerint: `BACKUP … WITH CHECKSUM` + `RESTORE VERIFYONLY` helyi átmeneti mappába (az SQL-szolgáltatásfiók soha nem ír hálózati célra), láncban restic / fájlmentés követi, **tömörítés nélkül, ha restic követi** (deduplikáció), az utolsó 1–2 `.bak` helyben marad, FULL recovery model figyelmeztetés, opcionális CHECKDB, msdb-ellenőrzés, saját service SID-re adott minimális SQL-jog (nem sysadmin).
6. **Munkamegosztás: a BackupForge ment, a BackupPulse felügyel.** Az app egyedül is teljes értékű (helyi e-mail, heti riport, belső watchdog), de a több gépes / offline-figyelés a BackupPulse dolga. Integráció: `status.json` szerződés (`STATUS-CONTRACT.md`), amit a BackupPulse-agent olvas.
7. **Üzleti irány: ingyenes app + fizetős felügyelet (BackupPulse) szolgáltatásként** — részletek: `BUSINESS.md`.
8. **Fejlesztés GitHubon; a spec-et a chat írja, a kódot Claude Code írja fázisonként** (ugyanaz a munkamódszer, mint a BackupPulse-nál). A spec eltéréseit a végrehajtó a `CLAUDE.md`-ben dokumentálja.
9. **Technológia: C# / .NET 10 LTS** (a .NET 8 támogatása 2026 novemberében lejár), WPF GUI, Inno Setup telepítő, SQLite előzmények, named pipe IPC.
10. **Munkanév: BackupForge** (a BackupPulse párja). Végleges név a telepítő-fázis (F7) előtt.

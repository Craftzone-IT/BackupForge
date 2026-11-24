# Státusz-szerződés: `status.json` (BackupForge → BackupPulse)

> Ez a dokumentum a BackupForge és bármely külső figyelő (elsősorban a **BackupPulse**) közötti szerződés. A BackupForge **ment**, a BackupPulse **felügyel**. A BackupForge nem tud a BackupPulse-ról: csak egy jól definiált, verziózott állapotfájlt ír. A BackupPulse Windows-agentje (`collect.ps1`) ezt olvassa, és a saját jelentésébe egy új `backupforge` blokkként teszi; a kiértékelés és a riasztás a BackupPulse szerverén történik (ahogy az FFS / Cobian / Veeam / sql_backup esetén is).

## 1. Miért fájl, és miért nem közvetlen API-hívás (döntés, 2026-11-24)
- A BackupPulse modellje: **eszközönként egy gyűjtő, push-only, nyers tények** → a szerver értékel. Egy második küldő ugyanarra az eszközre ütközne (hiányzó blokk → „nem érkezett adat”), és külön tokenkezelést igényelne.
- Az app így a BackupPulse nélkül is teljes értékű, és más figyelő (RMM, Zabbix, PRTG, saját szkript) is fel tudja dolgozni.
- Az offline-figyelést a BackupPulse már megoldja (a gyűjtő heartbeatje); a **szolgáltatás leállását** a `heartbeat` mező kora mutatja.
- **Később** (ha a BackupPulse-t szolgáltatásként áruljuk az ingyenes apphoz, és nem akarjuk, hogy a felhasználó külön agentet telepítsen): az appba beépített „BackupPulse-jelentő” ugyanezt a tartalmat közvetlenül POST-olja, saját eszköztípusként (`devices.kind = backupforge`). A tartalom ugyanez a séma marad — ezért kell már most stabilnak és verziózottnak lennie.

## 2. Hely, írás, olvasás
- Útvonal: `C:\ProgramData\BackupForge\status\status.json` (a `ProgramData` helyét `%ProgramData%`-ból kell feloldani).
- Kódolás: UTF-8 BOM nélkül. Időpontok: ISO 8601 **helyi idő eltolással** (`2026-11-24T14:30:00+01:00`) — ugyanúgy, ahogy a BackupPulse-agent küldi a többi időpontot.
- Írás: a szolgáltatás **percenként** (heartbeat) és minden futás-állapotváltáskor; atomikusan (temp fájl ugyanabban a mappában + `File.Replace`). Méret: célzottan < 64 KB (a hibalisták rövidítve).
- ACL: a `status\` mappa olvasható a SYSTEM és az Administrators számára (a BackupPulse-gyűjtő SYSTEM-ként fut). Titok nincs a fájlban.
- Olvasó oldal (BackupPulse `collect.ps1`): `ReadWrite`/`Delete` megosztással nyitja; ha a JSON nem értelmezhető (pl. épp cserélődik — elvileg az atomikus csere miatt nem fordulhat elő), egyszer újrapróbálja 1 mp múlva, utána `check_errors[]`-ba kerül.

## 3. Séma (`status_schema: 1`)
A gépi séma: `schema/status.schema.json` (a BackupForge repóban; a F3 fázisban készül, ennek a dokumentumnak megfelelően). Ismeretlen mezőket az olvasó hagyja figyelmen kívül; **mező törlése vagy jelentésváltozása csak `status_schema` növeléssel** történhet.

```json
{
  "status_schema": 1,
  "app": "BackupForge",
  "app_version": "1.0.0",
  "instance_name": "SRV01 mentés",
  "hostname": "SRV01",
  "service": {
    "started": "2026-11-24T06:02:11+01:00",
    "heartbeat": "2026-11-24T14:30:00+01:00",
    "config_ok": true,
    "config_error": null,
    "config_changed": "2026-11-20T10:12:00+01:00"
  },
  "jobs": [
    {
      "id": "company-mirror",
      "name": "Céges adatok → NAS",
      "type": "files",
      "enabled": true,
      "disabled_since": null,
      "schedule_text": "4 óránként 06:15-től",
      "expected_interval_h": 4,
      "next_run": "2026-11-24T18:15:00+01:00",
      "running": null,
      "last_run": {
        "run_id": 1842,
        "trigger": "schedule",
        "start": "2026-11-24T14:15:00+01:00",
        "end": "2026-11-24T14:21:37+01:00",
        "result": "ok",
        "summary": "312 másolva, 4 verziózva, 0 törölve · 1,2 GB",
        "files_copied": 312, "files_versioned": 4, "files_deleted": 0, "files_skipped": 0,
        "bytes": 1288490188,
        "errors": []
      },
      "last_ok": "2026-11-24T14:15:00+01:00",
      "last_not_ok": "2026-11-21T10:15:00+01:00",
      "runs_24h": 4, "not_ok_24h": 0,
      "stale": false,
      "target": { "path": "\\\\192.168.1.10\\backup\\srv01\\Company", "online": true, "free_gb": 812.4, "size_gb": 3726.0, "min_free_gb": 50 },
      "versions": { "path": "\\\\192.168.1.10\\backup\\srv01\\Company_Versions", "keep_days": 21, "folders": 84, "oldest": "2026-11-03T06:15:00+01:00", "size_gb": null },
      "delete_guard": { "tripped": false, "pending_approval": false }
    },
    {
      "id": "sql-main",
      "name": "SQL: SQLEXPRESS",
      "type": "sql",
      "enabled": true,
      "disabled_since": null,
      "schedule_text": "naponta 12:30, 22:30",
      "expected_interval_h": 14,
      "next_run": "2026-11-24T22:30:00+01:00",
      "running": null,
      "chain": { "on_success": ["sql-to-offsite"] },
      "last_run": {
        "run_id": 1840, "trigger": "schedule",
        "start": "2026-11-24T12:30:00+01:00", "end": "2026-11-24T12:41:10+01:00",
        "result": "warning",
        "summary": "4/4 adatbázis rendben · 1 figyelmeztetés: erp FULL recovery, nincs naplómentés",
        "bytes": 945002311,
        "errors": ["erp: FULL recovery model, de nincs naplómentés"]
      },
      "last_ok": "2026-11-24T12:30:00+01:00",
      "last_not_ok": null,
      "runs_24h": 2, "not_ok_24h": 0,
      "stale": false,
      "sql": {
        "instance": ".\\SQLEXPRESS",
        "edition": "Express Edition (64-bit)",
        "version": "15.0.4385.2",
        "dbs": [
          { "name": "erp", "system": false, "recovery_model": "FULL", "last_result": "ok", "last_ok": "2026-11-24T12:30:42+01:00",
            "last_size": 943718400, "median_ok_size": 941621248, "verify_ok": true, "last_full_any": "2026-11-24T12:30:42+01:00",
            "last_checkdb": "2026-11-23T01:00:00+01:00", "last_checkdb_ok": true, "error": null },
          { "name": "master", "system": true, "recovery_model": "SIMPLE", "last_result": "ok", "last_ok": "2026-11-24T12:30:05+01:00",
            "last_size": 4325376, "median_ok_size": 4325376, "verify_ok": true, "last_full_any": "2026-11-24T12:30:05+01:00",
            "last_checkdb": null, "last_checkdb_ok": null, "error": null }
        ],
        "staging": { "path": "C:\\ProgramData\\BackupForge\\staging\\sql-main", "free_gb": 120.5 }
      }
    },
    {
      "id": "sql-to-offsite",
      "name": "SQL-mentések → offsite",
      "type": "restic",
      "enabled": true,
      "disabled_since": null,
      "schedule_text": "láncban: sql-main után",
      "expected_interval_h": 14,
      "next_run": null,
      "running": { "since": "2026-11-24T12:41:12+01:00", "phase": "backup", "percent": 63.0 },
      "last_run": {
        "run_id": 1838, "trigger": "chain",
        "start": "2026-11-23T22:41:20+01:00", "end": "2026-11-23T22:44:02+01:00",
        "result": "ok",
        "summary": "snapshot 4f2a91c3 · 2 új fájl · +38 MB a tárolóban",
        "bytes": 39845888,
        "errors": []
      },
      "last_ok": "2026-11-23T22:41:20+01:00",
      "last_not_ok": null,
      "runs_24h": 1, "not_ok_24h": 0,
      "stale": false,
      "restic": {
        "repo_id": "offsite",
        "location_display": "sftp:storage.example.com:/srv01",
        "last_snapshot": "4f2a91c3",
        "snapshots": 41
      }
    }
  ],
  "repos": [
    {
      "id": "offsite",
      "location_display": "sftp:storage.example.com:/srv01",
      "append_only_server": false,
      "last_forget_prune": "2026-11-23T03:00:00+01:00", "last_forget_prune_result": "ok",
      "last_check": "2026-11-22T05:00:00+01:00", "last_check_result": "ok", "last_check_read_pct": 5,
      "restic_version": "0.18.1"
    }
  ],
  "targets": [
    { "id": "nas1", "type": "smb", "path": "\\\\192.168.1.10\\backup\\srv01", "online": true, "free_gb": 812.4, "size_gb": 3726.0, "min_free_gb": 50, "checked": "2026-11-24T14:30:00+01:00", "error": null }
  ],
  "notifications": { "email_enabled": true, "last_sent": "2026-11-21T10:22:00+01:00", "last_error": null }
}
```

### Mezőszabályok
- `result`: `ok` | `warning` | `failed` | `interrupted` | `skipped`.
- `last_ok` / `last_not_ok`: az utolsó `ok`/`warning` (sikeresnek számító) illetve `failed`/`interrupted` futás **kezdete**.
- `running`: `null`, ha nem fut; különben `{since, phase, percent|null}`. `phase`: `plan` | `copy` | `retention` | `vss` | `backup` | `verify` | `forget_prune` | `check` | `checkdb`.
- `stale`: az app belső watchdogja szerint (SPEC 10.2) — tájékoztató; a BackupPulse a saját küszöbével számol (`expected_interval_h`, `last_ok`).
- `errors`: legfeljebb 10 elem, elemenként legfeljebb 300 karakter.
- Ismeretlen / nem alkalmazható blokk: `null` (pl. `sql` egy `files`-feladatnál nem szerepel, vagy `null`).
- A `location_display` titkot nem tartalmazhat (felhasználónév, kulcs kivágva).

## 4. Javasolt szerveroldali kiértékelés (a BackupPulse CR-jének alapja, F8)
> A BackupPulse oldali CR-t (pl. `CR-010-backupforge.md`) a chat írja a BackupPulse repóba, ha a BackupForge eléri az F3-at. Itt csak az irány van rögzítve, hogy a státusz minden szükséges tényt tartalmazzon.

| check kulcs | szabály | lényeg |
|---|---|---|
| `bf:service` | `bf_service` | `heartbeat` kora > 10 perc → CRIT „a BackupForge-szolgáltatás nem fut”; `config_ok = false` → CRIT; különben OK (verzió) |
| `bf:job:<id>` | `bf_job` (kor-alapú) | letiltva → WARN „letiltva <disabled_since> óta” (nem OK!); `last_run.result = failed` és nincs utána sikeres → CRIT; `now − last_ok > expected_interval_h + türelmi idő` → CRIT; `warning` → WARN; `delete_guard.tripped` → CRIT; fut → OK „fut (… óta)” (30 percig érvényes, mint a többi agentnél); OK |
| `bf:sqldb:<job>:<db>` | `bf_sqldb` (kor-alapú) | a CR-005 `sqlbackup_db` mintájára: nincs sikeres → CRIT, kor → CRIT, hiba → WARN, gyanúsan kicsi → WARN, rendszer-db lazábban; FULL recovery naplómentés nélkül → WARN; CHECKDB hiba → CRIT |
| `bf:repo:<id>` | `bf_repo` | `last_check_result = failed` → CRIT; check régebbi, mint 14 nap → WARN; prune régebbi, mint 14 nap (ha nem append-only) → WARN |
| `bf:target:<id>` | a meglévő `target` szabály | offline → CRIT, `free_gb < min_free_gb` → WARN/CRIT a meglévő küszöbökkel |

A BackupPulse-gyűjtőben: a `config.json`-ba `"backupforge": { "status_path": "C:\\ProgramData\\BackupForge\\status\\status.json" }`; a telepítő magától felajánlja, ha a fájl létezik. A teljes `status.json`-t (vagy annak a fenti checkekhez szükséges részét) a jelentés `backupforge` blokkjába teszi, `as_list`-tűrő módon.

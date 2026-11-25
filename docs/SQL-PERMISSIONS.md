# SQL Server-jogosultságok (SPEC 8.6)

A szolgáltatás **LocalSystem**-ként fut, `unrestricted` service SID-del (`sc sidtype BackupForge unrestricted`, a telepítő állítja be). Így az SQL Server felé a **`NT SERVICE\BackupForge`** virtuális fiókként azonosítható, és a jogosultság erre adható — a `NT AUTHORITY\SYSTEM` login soha nem lesz sysadmin.

A szkriptet a `bf sql grant <jobId>` (vagy a GUI „Jogosultságok beállítása” gombja) állítja elő és — `--yes` után — futtatja **a bejelentkezett rendszergazda saját Windows-loginjával** (vagy `--login <sql-login>`, a jelszó stdin-ről). Ez a hitelesítés nem tárolódik, és a szolgáltatáshoz sem jut el.

## A minimális jogkészlet

| Mire kell | Jog | Forrás (Microsoft-dokumentáció) |
|---|---|---|
| kapcsolódás | `CREATE LOGIN [NT SERVICE\BackupForge] FROM WINDOWS` | CREATE LOGIN |
| `BACKUP DATABASE` | `db_backupoperator` szerepkör minden mentett adatbázisban (master, model, msdb is) | BACKUP: „BACKUP DATABASE and BACKUP LOG permissions default to members of the sysadmin fixed server role and the db_owner and db_backupoperator fixed database roles.” |
| `RESTORE VERIFYONLY` | `CREATE ANY DATABASE` (szerver szint) | RESTORE VERIFYONLY: „obtaining information about a backup set or backup device requires CREATE DATABASE permission” |
| felderítés (`sys.databases`, recovery model) | `VIEW ANY DATABASE` (alapból a `public`-é, a szkript kifejezetten is megadja) | sys.databases |
| ki mentett utoljára (msdb, SPEC 8.5), naplómentés-ellenőrzés (8.4) | `SELECT` az `msdb.dbo.backupset` táblán (msdb-felhasználó) | backupset |
| becsült mentési méret | a fenti `backupset` + `sys.master_files` (a `CREATE ANY DATABASE` miatt látható) | sys.master_files |
| `DBCC CHECKDB` (csak ha a feladatban `checkdb` be van állítva) | `db_owner` a felhasználói adatbázisokban | DBCC CHECKDB: „Requires membership in the sysadmin fixed server role or the db_owner fixed database role.” |

Nem kell és nem kap: `sysadmin`, `VIEW SERVER STATE` (a staging-mappa ACL-jéhez a példány alapértelmezett virtuális fiókját — `NT SERVICE\MSSQLSERVER` / `NT SERVICE\MSSQL$<NÉV>` — használjuk; ha a `sys.dm_server_services` olvasható, az onnan kiolvasott fiókot).

## Új adatbázis
`all_user` módban az új adatbázis automatikusan bekerül a mentésbe, de a jogot nem kapja meg magától: a futás az adatbázisra `failed` „nincs jogosultság (új adatbázis?) — futtasd a Jogosultságok beállítását”. Ilyenkor a `bf sql grant <jobId> --yes` újra lefuttatandó (idempotens).

## Átmeneti mappa
A `BACKUP`-ot az SQL Server szolgáltatásfiókja írja: a motor minden futás előtt **csak a feladat saját staging-mappájára** (`C:\ProgramData\BackupForge\staging\<jobId>`) ad Modify jogot ennek a fióknak.

## A szkript (példa)
```sql
USE [master];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT SERVICE\BackupForge') CREATE LOGIN [NT SERVICE\BackupForge] FROM WINDOWS;
GRANT CREATE ANY DATABASE TO [NT SERVICE\BackupForge];
GRANT VIEW ANY DATABASE TO [NT SERVICE\BackupForge];
USE [erp];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT SERVICE\BackupForge') CREATE USER [NT SERVICE\BackupForge] FOR LOGIN [NT SERVICE\BackupForge];
ALTER ROLE [db_backupoperator] ADD MEMBER [NT SERVICE\BackupForge];
USE [msdb];
…
GRANT SELECT ON [dbo].[backupset] TO [NT SERVICE\BackupForge];
```

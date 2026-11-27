# CR-003 — VSS: eszközútvonal `Query`-vel; SMB: minden hálózati elérés lease-ben

**Állapot: KÉSZ (2026-11-27)** — részletek: CLAUDE.md „CR-003 — KÉSZ” és „Eltérések a CR-003-tól”; a tesztgépes ellenőrzés a chaté.

Forrás: `docs/TEST-FINDINGS.md`, T3 (a CR-002 újratesztje a chat tesztgépén, tiszta Windows Server 2022 VM). A CR-002 két fontos pontja (A1 VSS, A2 SMB) a valós gépen továbbra sem működik, de mindkettőnek megvan az oka. A CR-002 többi pontja rendben van.

Sorrend: 1 → 2. Mindkettő külön commit (`CR-003 1: …`), teszttel. A CR akkor kész, ha:
- a CI zöld (Linux + Windows, 0 nem engedélyezett kihagyással);
- a `CLAUDE.md`-ben megvan a „CR-003 — KÉSZ” és az „Eltérések a CR-003-tól” szakasz;
- a `TEST-FINDINGS.md` H2 / H3 sora mellett ott a „→ CR-003 kész (ellenőrzésre vár (chat))” jelölés.

Az `AGENT-RUN.md` szabályai érvényesek, a repó nyilvános.

---

## 1. (FONTOS) VSS: az eszközútvonalat `Query`-vel kell olvasni (H2)

**Mért viselkedés (T3), writerekkel és `--no-writers`-szel is:**
- `GetSnapshotProperties(<snapshotId>)` → `S_OK`, de a `VSS_SNAPSHOT_PROP` csupa nulla, újraolvasás után is.
- A chat egy önálló C#-próbával kizárta a gyanúsítottakat:
  - **vtable:** a 37. hely `ImportSnapshots` (`0x80042311`), a 38. `BreakSnapshotSet` (`VSS_E_OBJECT_NOT_FOUND`), a 39. `GetSnapshotProperties` (`S_OK`, üres), a 40. `Query`; a sorrend tehát jó.
  - **`Guid`-átadás:** érték és `ref` szerint is ugyanaz az eredmény.
  - **kontextus:** egy `VSS_CTX_ALL` kontextusú második requester is üreset ad.
- **Ami működik:** `Query(GUID_NULL, VSS_OBJECT_NONE (1), VSS_OBJECT_SNAPSHOT (3), out IVssEnumObject)`, majd `IVssEnumObject.Next(1, VSS_OBJECT_PROP*, out fetched)`. Ez visszaadja a pillanatképet az egyező `SnapshotId`-vel és a helyes eszközútvonallal (`\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyNN`).

**Javítás (`BackupForge.Windows/Vss.cs`):**
- A `DeviceObject` a `DoSnapshotSet` után a `Query`-t használja.
  - Interfész: `IVssEnumObject`, IID `AE1C7110-2F60-11d3-8A39-00C04F72D8E3`, metódusok sorrendben: `Next`, `Skip`, `Reset`, `Clone`.
  - Felépítés: `VSS_OBJECT_PROP` = `int Type` + 4 bájt kitöltés + `VSS_SNAPSHOT_PROP` (union) a 8. bájttól.
  - Az eredményt `SnapshotId` szerint kell szűrni (a saját halmaz pillanatképeire; a `SnapshotSetId` is egyezzen).
  - Minden visszaadott elemre `VssFreeSnapshotPropertiesInternal(ptr + 8)`, a felsorolót `Marshal.ReleaseComObject`-tel kell elengedni.
- A `GetSnapshotProperties` maradhat első próbálkozásnak, de üres eredménynél (`SnapshotId == Guid.Empty`) a `Query` dönt. Az egyszerűbb, ha csak a `Query` marad; ezt a végrehajtó döntse el, és írja az `Eltérések` közé.
- A `bf diag vss` írja ki mindkét utat (a `GetSnapshotProperties` és a `Query` eredményét is).
- **A CI-teszt szigorítása:**
  - Ha a pillanatkép elkészült (writerekkel vagy anélkül), az eszközútvonalnak **kötelező** nem üresnek lennie, és a zárolt tesztfájlnak (`FileShare.None`-nal nyitva) a pillanatképből olvashatónak kell lennie.
  - A „VSS nélkül mentve” figyelmeztetéssel záruló futás a Windows-runneren hibás teszt, nem elfogadott eredmény.
  - Indok: a T3 szerint a writer nélküli ág is ugyanitt bukott, a CI mégis zöld volt.
- **Ellenőrzés (chat, tesztgép):** `bf diag vss C:\…` → 0; `when_available` fájlfeladat egy `FileShare.None`-nal zárolt fájllal → `ok`, és a zárolt fájl a célon van.

## 2. (FONTOS) SMB 1219: lease nélküli hálózati elérés a státuszírásban és a visszaállítás-böngészésben (H3)

**Mért viselkedés (T3):**
- `docs-nas` 5 percenként, 30 percen át: **7 futásból 4 `failed`** („…error 1219, not made by BackupForge”).
- A `Get-SmbConnection` szerint a szerverre **a gép saját fiókjával** (`WORKGROUP\<GÉPNÉV>$`) él egy kapcsolat. Ilyen akkor jön létre, ha a LocalSystem kapcsolat (lease) nélkül nyúl egy UNC-útvonalhoz.
- A CR-002 számlált kapcsolatkezelője rendben van. A hiba az, hogy **nem minden UNC-elérés megy rajta keresztül.**

**Ok (kódból):**
- `ServiceComposition.cs` (`Versions = job => files?.VersionsInfo(...)`) → `FileJobEngine.VersionsInfo` → `VersionsRetention.Info(_fs, path)`. Ez a `status.json` **percenkénti** írásakor lease nélkül járja be a hálózati cél Versions mappáját.
- `RestoreIpc` (`restore.versions` / `restore.browse`) és `FileRestoreEngine.Points`: szintén lease nélkül olvasnak (a `restore.run` már lease-ben fut).

**Javítás:**
1. **`VersionsInfo`: ne olvasson a célról a futáson kívül.** A fájlfeladat a futás végén, a saját lease-én belül számolja ki (mappák száma, legrégebbi), és tegye a kv-be (`versions_info:<jobId>`). A státuszíró és a heti riport ezt olvassa. Helyi célnál maradhat az élő számolás is, de egyszerűbb, ha egy kódút van. Ha még nem futott: `null` (a szerződés szerint megengedett).
2. **`restore.versions` / `restore.browse` / `FileRestoreEngine.Points`:** a célt `ITargetConnector.Connect`-tel kell elérni (lease), mint a `restore.run`-nál.
3. **Teljes átnézés:** keress meg minden helyet, ahol a szolgáltatás egy cél útvonalát (tükör, Versions, staging UNC-n, `path.check`, `target.test`, a szabad hely) a fájlrendszeren keresztül éri el. Mindegyik vagy lease-ben fusson, vagy ne nyúljon a célhoz.
4. **Védőháló, hogy ez ne térhessen vissza:** egy `LeaseGuardFileSystem` (`IFileSystem`-dekorátor). Ha egy UNC-útvonalat úgy ér el, hogy az adott szerverre nincs élő lease, akkor tesztben és debug-buildben kivételt dob, élesben WARN-t ír a naplóba, egyszer szerverenként óránként.
   - A „van-e élő lease” kérdést a `SmbConnectionManager` válaszolja meg.
   - Csak hitelesítéses (`username`-es) SMB-célra vonatkozik. Felhasználó nélküli UNC-célnál a LocalSystem saját hozzáférése a szándékolt működés.
5. **Tesztek:**
   - Linux: a `LeaseGuardFileSystem` (lease nélkül UNC → kivétel; lease-ben → rendben; helyi út → mindig rendben).
   - A státuszíró és a visszaállítás-böngészés egy hamis connectorral: a Versions-információ a kv-ből jön, és a célhoz lease nélkül nem nyúl.
   - Windows-CI, a meglévő helyi megosztással és helyi felhasználóval:
     - a szolgáltatás valódi folyamatként legalább 3 percig fut, 1 perces státuszírással;
     - közben egy hitelesítéses SMB-fájlfeladat legalább háromszor fut, köztük `restore.versions` és `restore.browse` hívással;
     - **minden futás `ok`**;
     - a `Get-SmbConnection` kimenetében a megosztás felé **soha nincs** a gép fiókjával (`<GÉPNÉV>$`) létrehozott kapcsolat (10 mp-enként ellenőrizve);
     - a végén nem marad kapcsolat.
6. **Ellenőrzés (chat, tesztgép):** a T3 forgatókönyve: 5 percenként, 30 percig, `Get-SmbConnection` 1 percenként.

## Apróság
- A szolgáltatásnapló egy leállításkor kétszer írja a „stopping” / „stopped” sort. Valószínűleg a host és a szolgáltatás leállítása is naplóz. Egyszer legyen.

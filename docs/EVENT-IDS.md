# Eseménynapló-azonosítók (Windows `Application` napló, forrás: `BackupForge`)

RMM-ekhez / figyelőkhöz. A forrást a telepítő regisztrálja; ha hiányzik, a szolgáltatás első induláskor (LocalSystem) létrehozza.

| ID | Szint | Mikor |
|---|---|---|
| 1000 | Information | a szolgáltatás elindult (verzió, feladatok száma) |
| 1001 | Information | a szolgáltatás leállt |
| 2001 | Warning | egy futás eredménye `warning` (üzenet: `<jobId>: <summary>`) |
| 2002 | Error | egy futás eredménye `failed` |
| 2003 | Error | törlési biztosíték: a futás nem törölt semmit (tömeges törlés lett volna) |
| 2004 | Error | belső watchdog: egy feladatnak régóta nincs sikeres futása (SPEC 10.2) |
| 2005 | Information | induláskor árván maradt VSS-pillanatképek törölve |
| 2010 | Error | a `config.json` hibás — a szolgáltatás az utolsó érvényes konfigurációval fut |
| 2020 | Error | az e-mail 4 próbálkozás (1, 5, 15 perc) után sem ment el |
| 2021 | Warning | a `heartbeat_url` hívása sikertelen |
| 2090 | Error | váratlan belső hiba |

Az üzenetek titkot nem tartalmaznak (a naplózás titok-maszkolón megy át).

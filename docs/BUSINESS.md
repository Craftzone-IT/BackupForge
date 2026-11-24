# Üzleti modell (vázlat, 2026-11-24)

> Ötletszint — nem a v1 fejlesztés része, de a technikai döntéseknél figyelembe kell venni (pl. a státusz-szerződés stabilitása, a titkok kezelése, a több bérlős BackupPulse).

## Modell: open core
- **BackupForge (app): ingyenes**, egy gépen teljes értékű, korlátozás nélkül: fájlmentés + Versions, restic, SQL, helyi e-mail-riasztás, heti riport, belső watchdog. Mesterséges lebutítás nincs — egy mentőprogramnál ez bizalomvesztés.
- **BackupPulse (felügyelet): fizetős szolgáltatás** az app mellé — amit egy gép magáról nem tud:
  - több gép / több ügyfél egy dashboardon,
  - **offline-riasztás** (a gép vagy a szolgáltatás leállt) — ez a legerősebb érv,
  - előzmények, trendek (méret, időtartam),
  - ügyfélnek szóló havi riport (magyarul),
  - ticketrendszer-integráció (Freshdesk), webhook / push.

## Licenc
- App: **MIT** (vagy Apache 2.0) — a legnagyobb bizalom, a restic BSD-2 licence mellé illik. A GPL nem akadályozná meg, hogy valaki átnevezve árulja, csak a forrás közzétételét kényszerítené; a valódi védelem a hosztolt szolgáltatás minősége.
- BackupPulse szerver: külön döntés. Ha nyílt, a healthchecks.io-modell: saját telepítéssel ingyenes, hosztolva fizetős.
- A repó a v1-ig **privát**.

## Amit a BackupPulse-nak tudnia kell az eladhatósághoz (később, külön spec)
- **Több bérlő (multi-tenant)**: bérlőnként elkülönített ügyfelek, eszközök, felhasználók, API-kulcsok.
- **Előfizetés-kezelés**: kártyás fizetés (Barion), csomagok gépszám-sávokkal, próbaidőszak.
- **Beépített jelentő az appban** (`STATUS-CONTRACT.md` 1. pont), hogy az ingyenes app felhasználójának ne kelljen külön agentet telepítenie: az appban egy „Csatlakozás a BackupPulse-hoz” gomb + token.
- **Adatminimalizálás**: csak metaadat megy fel (feladatnév, eredmény, méret, időpont) — fájlnév / útvonal ne (vagy opcionálisan, a felhasználó döntése). Adatfeldolgozói szerződés (DPA) kell, de nem ügyféladatot kezelünk.

## Felelősség, támogatás
- ÁSZF: a felügyelet riaszt, de nem garantálja a mentés sikerét / visszaállíthatóságát.
- Ingyenes felhasználók: GitHub issues / fórum; e-mailes támogatás csak előfizetőknek (egyszemélyes MSP mellett ez kritikus).

## Piac
- Magyar KKV-k, kisebb rendszergazdák, IT-s egyéni vállalkozók, akik Cobian / FFS / Macrium Free-ről állnak át, gyakran SQL Express-szel. Előny: magyar felület és dokumentáció, ismert helyi környezet.
- Árazás: gépenként havidíj, sávokkal — a számokat a pilot után.

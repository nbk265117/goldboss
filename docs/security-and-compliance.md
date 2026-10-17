# Security and compliance

## Ankama rules reviewed (17/10/2026)

Sources fetched: DOFUS Terms of Use / CGU (https://www.dofus.com/fr/cgu, "Dernière mise à jour : Août 2025"),
game rules page (https://www.dofus.com/fr/mmorpg/communaute/reglement), Ankama support articles on bots and
reportable behaviour (support.ankama.com, updated June 2026), and the DOFUS 3.0 launch article.

What they clearly prohibit (CGU 5.2.1, 5.2.5, 5.2.6, 5.2.9; rules page; support):
reverse engineering/modifying the client or any file in its folder; bots, automation, auto-clickers, any
program that alters the game experience or bypasses its rules; packet sniffing/protocol emulation; programs
that modify account characteristics; bypassing the multi-account block. Sanction: permanent ban.

What is unclear: the CGU also forbid "autres logiciels non autorisés", and the rules page bans unauthorised
third-party programs "quel qu'en soit l'usage"; the client "may contain features to detect unauthorised
programs running alongside". Ankama has published **no list of tolerated tools and no statement on overlays
or screen reading**. CGU 13.2/13.5 restrict copying/extracting game data without written consent.

**Consequently this app is not "authorised" by Ankama, and says so in its UI.** Only Ankama's written consent
would change that. Its behaviour is chosen to stay far from every explicit prohibition:

| Prohibited / risky | Dofus Gold Boss - DGB |
|---|---|
| Client modification, injection, hooks, memory reading | None. The app never opens, enumerates or attaches to the game process. |
| Automation, inputs to the game | None. No `SendInput`/`SendKeys`/`PostMessage` exists in the code. Global hotkeys only control the companion; plain keys are never registered. UI tests drive only the companion's own windows through UI Automation. |
| Network interception | None. The app makes no network request at all; only the user's browser opens a source link. |
| Anti-cheat bypass | None. |
| Screen reading | One-shot GDI capture (like Print Screen), **allowed by default since 0.5.1 but user-triggered only** (Detect/Capture button or hotkey; can be turned off), analysed in memory, not stored unless enabled. Image import is the primary workflow. |
| Game data / assets | No game asset is bundled. Mechanics are paraphrased from guides, attributed, with short quotes. The DofusDB importer is separate and run by the user, who decides whether it suits DofusDB's terms and the CGU. |
| Promotion on Ankama channels (CGU 6.8) | Not done by the app; do not advertise it on Ankama services. |

## Application security

- **No elevation**: manifest `asInvoker`; data only under `%LOCALAPPDATA%\DofusGoldBoss` (or `--data-dir`).
- **No telemetry**, no outbound connection. Source links: only absolute `http(s)` URLs are opened, on click.
- **Imports** are data only: size limit, JSON Schema with `additionalProperties: false`, semantic checks, no
  file paths in the format, no code execution; applied in a transaction after preview.
- **Images**: signature check, file ≤ 30 MB, ≤ 40 Mpx checked before allocating pixels; malformed files are
  rejected with a message.
- **Paths**: database path must be a `.db` file; exports are written only where the user chooses
  (atomic temp + move); settings are written atomically and a corrupt file is backed up, not lost.
- **Privacy**: no credentials or account data are ever requested. History, screenshot retention, alias
  learning and search history are opt-in, with a one-click delete. Logs contain no OCR text or images at the
  default level; the diagnostic export is shown for review, redacts the Windows user name, and is only saved
  where the user chooses.
- **Dependencies**: pinned centrally, licenses reviewed (see `LICENSE-NOTICES.md`), no known vulnerability
  (`dotnet list package --vulnerable --include-transitive`, 17/10/2026).

## Web sources used for the bundled data

- Dofus pour les Noobs: robots.txt allows the dungeon/guide pages; pages fetched one at a time with ≥ 3 s
  delay and an identifying User-Agent; content paraphrased with attribution.
- DofusDB: robots.txt disallows AI agents (ClaudeBot, Claude-User…) site-wide. It was therefore **not used**
  for the bundled data (one 2-record API request was made before that rule was read; nothing from it was
  kept). The user-run importer exists at the user's request.

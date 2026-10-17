# Known limitations

## Knowledge coverage

- Boss mechanics cover the 131 dungeons listed by Dofus pour les Noobs and 80 wanted-notice targets, from a
  single community guide. **382 of 604 mechanics come from guides last updated before Dofus 3** and are shown
  as "à revérifier"; 69 come from undated pages. Behaviour may have changed in Dofus 3.
- 44 mechanics are COMMUNITY_REPORTED because the page was vague, self-contradictory or flagged for revision.
  Some placements exist only as images in the guides and are not described.
- Regular (non-boss) monsters, spells, classes, quests and achievements are **not** bundled. The DofusDB
  importer can add monster/dungeon/area names and levels; its field mapping for monsters/areas could not be
  confirmed against the live API during development.
- Guild expeditions and event variants are mostly not covered.

## Recognition

- Text only: recognizes names readable on screen (tooltips, timeline, interface). It does not identify
  monsters from their appearance and does not read hidden state (HP, states, turn).
- Accuracy is measured on synthetic images only (see testing.md); real screenshots with decorative fonts,
  outlines or overlapping effects will do worse. Crop to the name for best results.
- Homonyms (e.g. two bosses called *Guerre*) are reported as ambiguous and require a choice.
- Windows OCR languages depend on installed Windows features (fr-FR and ar-SA on the development machine).

## Overlay

- Exclusive fullscreen games can hide topmost windows and make GDI capture black: use windowed or borderless.
- With "never take focus on click" enabled, the overlay search box cannot receive typing (Windows does not
  deliver keyboard input to a non-activating window); use the main window to search.
- In click-through mode the overlay ignores the mouse entirely; the hotkey (default Ctrl+Shift+F10) or
  "show/hide" restores it. Hiding the overlay also turns click-through off.
- Mixed-DPI multi-monitor setups: positions are stored in device-independent units and validated against the
  virtual screen, but were only tested on a single 1920×1080 screen.
- Global hotkeys can conflict with other programs; failures are shown in the status bar and Diagnostics.

## Distribution: unsigned binaries and Smart App Control

The release is **not code-signed**. With Windows 11 *Smart App Control* ("Contrôle intelligent des
applications") on, every new unsigned build was eventually blocked on the development PC (0x800711C7, for the
`.exe`, the `.dll`s and a single-file publish alike) until the user turned it off. Microsoft states there is no
per-app exception, and SAC checks the signature of every module (exe and DLLs); self-signed certificates do not
help. SmartScreen also warns on new unsigned downloads (users can "Unblock" the zip or choose "Run anyway").

Fix for everyone: Authenticode-sign all binaries with a certificate chaining to Microsoft's Trusted Root
Program — SignPath Foundation (free for OSI-licensed open source with an automated GitHub Actions build), Azure
Artifact Signing ($9.99/month, individuals only in the USA/Canada), or a commercial OV/EV certificate.
The README explains the user-side workarounds with a screenshot.

## Other

- Startup after first unzip is slow (18–51 s measured) because data is installed and Windows scans the files;
  later starts are ≈ 2 s.
- No installer, no auto-update, no tray icon. Closing the main window exits the application.
- The UI is in French and English; Arabic/Darija is not provided (the localization system supports adding a
  `Localization/<lang>.json` file).
- Once, during testing, a second launch immediately after killing the first exited without a log; it could
  not be reproduced afterwards.

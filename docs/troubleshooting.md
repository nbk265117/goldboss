# Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "OCR impossible : … langue 'fr-FR' n'est pas installée" | Install the OCR feature of the language: *Settings › Time & language › Language & region › French › Language options › Optical character recognition*, or pick an installed language in Settings › Recognition. |
| Recognition finds nothing | Crop to the monster name (drag on the preview); make sure the name is readable; keep "try several preprocessing steps" on. If the entity is not in the database, choose it manually or add it. |
| Wrong boss proposed | Click *Reject*, pick the right one in *Manual selection*. Enable "learn from my confirmations" to remember the OCR spelling. |
| Capture button greyed / "capture disabled" | Capture is off by default: Settings › Capture & privacy. |
| Capture is black | The game is in exclusive fullscreen; switch it to windowed/borderless. |
| Overlay invisible over the game | Same cause, or the overlay is hidden: Ctrl+Shift+F9 or the main window button. It does not appear in screenshots on purpose (option "exclude the overlay from captures"). |
| Overlay does not react to the mouse | Click-through is on (banner shown): Ctrl+Shift+F10. |
| "Le raccourci … est déjà utilisé" | Another program owns the combination; change it in Settings › Shortcuts. |
| "La base de connaissances est endommagée" | Answer *Yes*: the file is kept as `knowledge.db.corrupt-<date>` and a fresh one with the bundled data is created. Your manual entries are in the old file (export them from a backup if needed). |
| "…ne peut pas être mise à jour" (migration) | The database comes from a newer version of the app: update the app or point Settings › Database to another file. |
| Import refused | The error list shows the JSON path and rule; fix the package (see knowledge-import-export.md). Nothing was changed. |
| Settings reset at start | `settings.json` was unreadable; the original is kept as `settings.json.bak`. |
| Logs / report for a bug | Diagnostics › *Prepare a report…*, review, save, share. Logs: `%LOCALAPPDATA%\DofusGoldBoss\logs`. |

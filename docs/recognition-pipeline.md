# Recognition pipeline

Code: `Application/Recognition/RecognitionService.cs`, `Application/Matching/*`, `Application/Imaging/*`,
`Ocr.Windows/*`. Everything runs locally; no image leaves the machine.

## Inputs

| Mode | Notes |
|---|---|
| Manual search | Main window and overlay, debounced |
| Image file | PNG, JPEG, BMP, GIF, TIFF; ≤ 30 MB; ≤ 40 Mpx; signature checked before decoding |
| Clipboard | e.g. after `Win+Shift+S` |
| Screen capture | **Opt-in** (Settings › Capture & privacy). One GDI BitBlt of the monitor that holds the overlay, only on click/hotkey; paused with the overlay's pause button. The overlay is excluded (`SetWindowDisplayAffinity`) only for the duration of this capture, so it is never OCR'd yet stays visible to Discord, OBS and other screen sharing. |
| Region | Drag a rectangle on the preview (Analysis tab) to OCR only that part |

## Steps

1. **Crop** to the selected region (clamped to the image).
2. **Pad** crops and images smaller than 200 px with a 24 px border of the edge colour. Measured: without
   it, a tight crop around one line of text returned *no text at all* from Windows OCR.
3. **Preprocessing variants**, tried in order, stopping at the first confident match:
   `original` → `upscale-contrast` (×2, grayscale, 1–99 % stretch) → `upscale-inverted-threshold` (for light
   text on dark UI; Otsu) → `upscale-sharpen`. Scaling respects the engine maximum (10 000 px reported).
4. **OCR** via `IOcrProvider` (Windows.Media.Ocr; 30 s timeout; cancellable). Errors (no engine, language not
   installed, timeout) are reported in the UI with the fix, never turned into results.
5. **Entity matching** (`EntityMatcher.FindInText`) over each OCR line:
   - normalization: NFKD, accents removed, ligatures œ/æ/ß expanded, lower-case, punctuation → space;
   - token windows of the name's length ±1 (so "Kralamoure Géant (Niv. 1000)" matches "Kralamoure Géant");
   - trigram pre-filter, then score = max(edit similarity, token-sorted edit similarity);
   - names shorter than 4 characters are matched exactly only.
6. **Classification** (thresholds in Settings): candidates < 0.75 discarded; if the two best entities read
   from the same line are within 0.05 → **Ambiguous** (nothing selected); best ≥ 0.92 → **Confident**
   (pre-accepted, still correctable); otherwise **NeedsConfirmation**.
7. **Results** list every candidate with: entity, type, score, the OCR text it came from, the matched name,
   the variant, and the text region (drawn on the preview). The user can confirm, reject, or pick another
   entity by search. Confirmed entities are added to the fight context.

## Learning (opt-in)

With "learn from my confirmations" enabled, a confirmed OCR reading that differs from every known name is
stored as a local alias (`origin = User`). Off by default.

## What it does not do

It does not identify monsters from their sprites, does not read hidden state (HP, states, turn order) and
does not claim the map or fight context from pixels: it reads visible text and matches it against known
names. The `IOcrProvider` / matcher boundary allows a future, properly licensed image model to be added
separately.

## Measured accuracy

See [testing.md](testing.md): on the 9-image synthetic fixture set, recall 100 %, precision 90.9 % (the only
"false positive" is a legitimate ambiguity between two bosses named *Guerre*), mean CER 0 %. No accuracy
figure is claimed for real game screenshots: no annotated screenshot dataset exists yet.

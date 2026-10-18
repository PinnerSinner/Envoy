# Compatibility

Which supported titles, display modes, DPI settings and Windows versions have been verified with which Envoy release
(contract §8.3, REQ-QSC-38). CI runs only on Windows Server (the `windows-latest` runner), so **this file is the only
record of desktop Windows 10 and 11 coverage**. It is filled in by the
[pre-release manual pass](releasing.md#pre-release-manual-pass): each run updates the cells it covered.

## How to read and fill it

- `✓ vX.Y.Z`: verified working with that release (chat read, translated and shown in the overlay; a reply typed into
  game chat).
- `✗ vX.Y.Z (note)`: verified **not** working with that release; the note says what fails and what the user sees.
  Exclusive fullscreen is expected to be `✗`: Windows hands Envoy a black capture, and Envoy shows the
  "likely exclusive fullscreen" hint (`CaptureText.LikelyExclusiveFullscreen`) suggesting borderless.
- `—`: not verified yet.

Rules:

- Before 1.0, every cell is filled (`✗` with a note is allowed).
- Every later release re-verifies at least the **Borderless** row at 100 % for each title on one Windows version.
- `Envoy.exe --ocr-e2e report.md` (step M-QSC-6) on Windows 10 22H2 verifies the OCR path on that OS without a game.
- `python3 tools/ci/check_policy.py` (`required-docs`) checks that every title × mode row exists and that each cell
  has one of the three forms.

## Matrix

| Title | Display mode | Win10 22H2 100 % | Win10 22H2 150 % | Win11 100 % | Win11 150 % |
| --- | --- | --- | --- | --- | --- |
| Age of Empires: Definitive Edition | Windowed | — | — | — | — |
| Age of Empires: Definitive Edition | Borderless | — | — | — | — |
| Age of Empires: Definitive Edition | Exclusive fullscreen | — | — | — | — |
| Age of Empires II: Definitive Edition | Windowed | — | — | — | — |
| Age of Empires II: Definitive Edition | Borderless | — | — | — | — |
| Age of Empires II: Definitive Edition | Exclusive fullscreen | — | — | — | — |
| Age of Empires III: Definitive Edition | Windowed | — | — | — | — |
| Age of Empires III: Definitive Edition | Borderless | — | — | — | — |
| Age of Empires III: Definitive Edition | Exclusive fullscreen | — | — | — | — |
| Age of Empires IV | Windowed | — | — | — | — |
| Age of Empires IV | Borderless | — | — | — | — |
| Age of Empires IV | Exclusive fullscreen | — | — | — | — |
| Age of Mythology: Retold | Windowed | — | — | — | — |
| Age of Mythology: Retold | Borderless | — | — | — | — |
| Age of Mythology: Retold | Exclusive fullscreen | — | — | — | — |

## Notes

Record anything a cell's note does not fit, with the release and date: driver or overlay quirks, a title's chat
scaling option, monitors that needed a second calibration.

# VirusTotal report

Every release `Zilch.exe` is uploaded to [VirusTotal](https://www.virustotal.com) (70+ antivirus engines)
by the [virustotal workflow](.github/workflows/virustotal.yml), and the report links are added to the release notes.

## v1.0.0

| File | Size | SHA-256 |
|---|---|---|
| `Zilch.exe` | 153,600 bytes | `DB985F68589CAF276502A474DB49DE383FBE0261E8F9703BBC5C3BD380600BF6` |

Report: https://www.virustotal.com/gui/file/db985f68589caf276502a474db49de383fbe0261e8f9703bbc5c3bd380600bf6

## Check it yourself

1. Download `Zilch.exe` from the release.
2. Get its hash in PowerShell:
   ```powershell
   Get-FileHash .\Zilch.exe -Algorithm SHA256
   ```
3. It must match the hash above. Open the report link, or drop the file on virustotal.com.

## About possible detections

Zilch is not code-signed and, by design, does things cleaners do: deletes files, edits the registry,
removes apps, and in Sudo mode takes ownership of protected files (`takeown`, `icacls`).
A few heuristic / "generic" engines can flag tools like that. The full source is in this repo:
you can read it and build the exact same exe with `build.ps1`.

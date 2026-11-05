# VirusTotal report

Every release `Zilch.exe` can be checked on [VirusTotal](https://www.virustotal.com) (70+ antivirus engines).
The [virustotal workflow](.github/workflows/virustotal.yml) uploads release files automatically once the repo has a `VT_API_KEY` secret.

## v1.0.0

| File | Size | SHA-256 |
|---|---|---|
| `Zilch.exe` | 153,600 bytes | `DB985F68589CAF276502A474DB49DE383FBE0261E8F9703BBC5C3BD380600BF6` |

Report: https://www.virustotal.com/gui/file/db985f68589caf276502a474db49de383fbe0261e8f9703bbc5c3bd380600bf6?nocache=1

**Result: 2 of 71 engines flagged it, 69 found nothing.**

| Engine | Label |
|---|---|
| Bkav Pro | `W32.Malware.3AA51018` |
| VBA32 | `Trojan.MSIL.InfoStealer.gen.B` |

The other 69 engines found nothing.

### Why those two are false positives

Both labels are *generic* ones (`gen`, a hash-style ID), not a known piece of malware. They come from heuristics, which
guess from what a program *looks like* it could do. Zilch looks like a lot, because cleaning a PC means touching what malware touches:

- **"InfoStealer"**: Zilch reads browser folders (Chrome, Edge, Brave, Opera, Firefox profiles) to measure and clear their *caches*,
  walks every AppData folder looking for leftovers, and reads the installed-programs list from the registry.
  An info-stealer reads the same folders for passwords and cookies. Zilch never opens those files, and it has no network code:
  it doesn't send anything anywhere.
- **Hidden PowerShell**: v1.0.0 passed its PowerShell commands (Windows app removal) as a base64 `-EncodedCommand`,
  a pattern malware loves. **Since v1.1.0 the script is passed as plain text** over standard input, no encoded blob.
- **Ownership and deletes**: Sudo mode runs `takeown` / `icacls` and schedules locked files for deletion on reboot.
- **Not code-signed**: a signing certificate costs money every year; small open-source tools usually don't have one,
  and unsigned .NET programs get less benefit of the doubt.

You don't have to trust this text. The full source is in this repo, GitHub Actions builds and self-tests every commit
(see the Actions tab), and you can build the exe yourself with `build.ps1`.

If you run an antivirus that flags Zilch, a false-positive report to that vendor helps everyone:
[Bkav](https://www.bkav.com) · [VBA32](https://www.anti-virus.by/en/).

## Check it yourself

1. Download `Zilch.exe` from the release.
2. Get its hash in PowerShell:
   ```powershell
   Get-FileHash .\Zilch.exe -Algorithm SHA256
   ```
3. It must match the hash above. Open the report link, or drop the file on virustotal.com.

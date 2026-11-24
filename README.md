# Valle Brote Updater

A Windows updater and repair utility for Valle Brote. It verifies the release manifest and download, backs up files before replacing them, and restarts the game after a successful update. The game invokes the updater from its main menu. The executable can also run as a repair utility.

This repository contains the updater, its manifest verification code and the public update channel configuration. The game is distributed separately. The updater requires Windows with .NET Framework 4.8 or later.

## Download and removal

The existing Windows updater is available in the [Valle Brote downloads](https://github.com/edelapaz/ValleBrote-Releases/releases/tag/v0.14.1). A standalone release built from this repository can attach its own `ValleBroteUpdater.exe` to GitHub Releases.

The updater is portable. Close it before removing the standalone executable. For a copy installed in its own game folder by the repair utility, close the game and remove that installation folder. Keep the player profile and any update backups you want to retain.

## Build

Run `powershell.exe -NoProfile -File .\Build.ps1` from the repository root. The script compiles the updater, checks its Windows version metadata, and runs its existing self-test against temporary fixtures under `Logs/`. It writes the executable to `dist/ValleBroteUpdater.exe`. GitHub Actions runs the same script and keeps the binary as a workflow artifact.

## Code signing policy

This project intends to use [SignPath.io](https://signpath.io/) for free code signing with a certificate provided by [SignPath Foundation](https://signpath.org/), subject to acceptance. The current GitHub Actions build artifact is unsigned unless the signing workflow completes.

- The author and reviewer is [edelapaz](https://github.com/edelapaz). Maintainers review changes from external contributors before merging.
- The release approver is [edelapaz](https://github.com/edelapaz). An approver reviews each signing request.
- Multi-factor authentication is required for GitHub and SignPath access.
- The updater downloads release metadata and binaries from the configured GitHub Releases channel. It does not send the save backup or other game files to the server. The backup remains on the computer. GitHub handles download requests under its [privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

See [SignPath setup and release verification](SIGNPATH.md) for the required configuration.

## License

The updater source and this standalone project are licensed under the [MIT License](LICENSE).

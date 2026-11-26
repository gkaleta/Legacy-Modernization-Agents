**Last updated**: 2026-11-26

# Building behind a download block

`Microsoft.Copilot.SDK` 2.x downloads the Copilot CLI runtime from GitHub releases **at build time**. On a machine where that download is blocked, the build fails before any code is compiled:

```
error MSB3923: Failed to download file
"https://github.com/github/copilot-cli/releases/download/v1.0.114/github-copilot-1.0.114-darwin-arm64.tgz"
```

The same failure shows up during `./doctor.sh setup` as *Could not fetch user-specific models, falling back to CLI model list*, because the model list is fetched by building and running the app.

The download comes from `github.com`, which redirects to `release-assets.githubusercontent.com`. It does not use the npm registry, and `.npmrc` or `npm config` have no effect. (Older versions of this page described an npm download; the SDK no longer uses one, and the `CopilotNpmRegistryUrl` property is ignored.)

## doctor.sh handles it

Before its first build, `doctor.sh` checks whether the download for the CLI version the app is built for is reachable. If it is not, it uses the native binary of an installed Copilot CLI (sets `CopilotCliBinaryPath`) and says so. If there is none, it prints the options below.

Nothing is checked once a build has downloaded the runtime, or when `CopilotCliBinaryPath` or `CopilotSkipCliDownload` is already set. Plain `dotnet build` and `dotnet run` commands need one of the options below.

## Option 1 — allow the download, or use a proxy

Allow `github.com` and `release-assets.githubusercontent.com`, or set `HTTPS_PROXY` to a proxy that can reach them.

## Option 2 — download from an internal mirror

If your organisation mirrors the `github/copilot-cli` releases, point the build at the mirror. It must keep the same layout (`<base>/v<version>/github-copilot-<version>-<platform>.tgz` with `SHA256SUMS.txt` next to it):

```bash
export COPILOT_CLI_DOWNLOAD_BASE_URL=https://<your-mirror>/copilot-cli/releases/download
dotnet build
```

Or per invocation: `dotnet build -p:CopilotCliReleaseBaseUrl=https://<your-mirror>/copilot-cli/releases/download`.

## Option 3 — build against an already-installed CLI

Where nothing can be downloaded, point the build at a Copilot CLI that is already on the machine. The download step is skipped entirely:

```bash
dotnet build -p:CopilotCliBinaryPath=/path/to/copilot
```

This is the option for air-gapped environments.

The path must be the **native binary**, not the `copilot` command on your `PATH`. npm and Homebrew installs put a Node loader script there, and the SDK copies the file into the build output, where the script cannot run. The native binary sits next to it:

```bash
# macOS on Apple silicon; use darwin-x64, linux-x64, linux-arm64 or win32-x64 elsewhere
export CopilotCliBinaryPath="$(npm root -g)/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot"
```

An environment variable works for every `dotnet` command, because MSBuild reads environment variables as properties. Keep the installed CLI at least as new as the version the app is built for (`./doctor.sh doctor` shows both); an older one can fail with `SDK protocol version mismatch`.

## Verifying

A successful download leaves the runtime under `obj/`, with a completion marker:

```
obj/Debug/net10.0/copilot-cli/<version>/<platform>/.copilot-runtime-complete
```

Either way, the build copies the runtime to `bin/Debug/net10.0/runtimes/<rid>/native/`, which is where it runs from. The app starts it as a separate process.

The expected CLI version is compiled into the assembly as metadata, so a mismatch between the CLI the SDK was built against and the one in use can be reported directly rather than surfacing as a protocol error during the handshake.

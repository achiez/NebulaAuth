# DevTools

Debug-only tooling: view previews, demo data and debug hooks. The folder is compiled in the `Debug` configuration
only (`NebulaAuth.csproj` removes it otherwise), so nothing here exists in a release build. Production code reaches it
through `partial` methods named `Debug*`; without this folder those calls compile to nothing.

| Folder | Contents |
|---|---|
| `Previews/` | `DebugPreview` and the `CreatePreview(string? arg)` factory of each view |
| `Demo/` | Main window demo states, fake confirmations, the in-memory Nebula Link, placeholder images |
| `Hooks/` | Implementations of the `Debug*` partial methods, the embedded browser script driver |
| `StyleGallery/` | Every mapped control style on one page |

All switches are environment variables read at startup.

## Previews and demos

| Variable | Effect |
|---|---|
| `NEBULA_PREVIEW` | Full type name of a view (e.g. `NebulaAuth.View.SettingsView`): opened in the root dialog host after startup |
| `NEBULA_PREVIEW_ARG` | State of that preview; the values are documented on the view's `CreatePreview` |
| `NEBULA_MAIN_DEMO` | Comma-separated main window states; the list is on `MainWindow.ShowMainDemoIfRequested` |
| `NEBULA_STYLE_GALLERY=1` | Replaces the main content with the style gallery |
| `NEBULA_MOTION_SLOW=<factor>` | Slows every animation down |
| `NEBULA_MOTION_TRACE=1` | Frame log of popup animations (`motion-trace.log` in the data folder) |
| `NEBULA_DEBUG_PASSWORD_HINTS=caps` | Password fields report Caps Lock as on |

## Storage

| Variable | Effect |
|---|---|
| `NEBULA_DEBUG_STORAGE_PASSWORD` | Unlocks an encrypted storage without the prompt |
| `NEBULA_DEBUG_STORAGE_ACTION` | `enable:<password>`, `disable` or `change:<old>:<new>` after the startup reconciliation |
| `NEBULA_DEBUG_STORAGE_SLOW=<ms>` | Delay after every file of a bulk storage operation |
| `NEBULA_DEBUG_UNLOCK_TRY` / `NEBULA_DEBUG_UNLOCK_TEXT` | Submits / only types a password in the unlock window |
| `NEBULA_DEBUG_CLIPBOARD_LIFETIME=<seconds>` | Lifetime of the readable "Copy mafile" copy |
| `NEBULA_DATAROOT_SIM_OS`, `_SIM_BASEDIR`, `_SIM_USERDATA` | Simulates the data folder rules of another OS / layout |

`StorageFaults.Hook` lets a test fail the storage layer at its named points.

## Updates

| Variable | Effect |
|---|---|
| `NEBULA_UPDATE_FEED` | Feed base URL instead of the repository (http allowed) |
| `NEBULA_UPDATE_PUBKEY` | Path of a PEM public key used instead of the pinned one |
| `NEBULA_UPDATE_AUTO=download\|apply` | Presses the update dialog buttons |
| `NEBULA_UPDATE_MANUAL=1` | The startup check behaves like "Check for updates" |
| `NEBULA_DIAG_REPORT=<ms>` (+ `NEBULA_DIAG_EXIT=1`) | Writes a diagnostics report to the data folder, then optionally exits |

## Embedded browser

| Variable | Effect |
|---|---|
| `NEBULA_BROWSER_TEST=<script>` / `NEBULA_BROWSER_TEST_OUT` | Runs a browser script (commands: `BrowserDebugScript`) and writes its report |
| `NEBULA_DEBUG_BROWSER_FAIL_START`, `_START_TIMEOUT_MS`, `_BAD_ENGINE`, `_EXPECT` | Simulated engine start failures / another expected account |

## Nebula Link

| Variable | Effect |
|---|---|
| `NEBULA_LINK_APIS` | Comma-separated API bases instead of the published list (empty: not available) |

The switches of the protocol library are listed in `NebulaAuth.NebulaLink/DevTools/README.md`.

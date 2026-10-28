# DevTools

Debug-only tooling of the Nebula Link library. The folder is compiled in the `Debug` configuration only
(`NebulaAuth.NebulaLink.csproj` removes it otherwise). Production code reaches it through `partial` methods named
`Debug*`; without this folder those calls compile to nothing, so a release build cannot relax an address check.

| File | Contents |
|---|---|
| `NebulaLinkApis.Debug.cs` | Addresses of local stands |
| `NebulaLinkIntegration.Debug.cs` | Scripted steps, the state file, the in-memory preview integration (`CreatePreview`) |
| `PreviewState.cs` | State setters used by the preview integration |

| Variable | Effect |
|---|---|
| `NEBULA_LINK_TENANT_URL` | API base of a local stand: allowed for pairing (http too) and used for a bare pairing code |
| `NEBULA_LINK_ALLOW_UNTRUSTED=1` | Any http(s) address can be paired with; an `apiOrigin` mismatch is only logged |
| `NEBULA_LINK_SCRIPT` | Steps run once after startup, separated by `;` (see `RunDebugStepAsync`) |
| `NEBULA_LINK_STATE_FILE` | Path of a JSON summary of the integration, rewritten every 500 ms |

Loopback addresses (`localhost`, `127.0.0.1`, `[::1]`) are always accepted in a Debug build.

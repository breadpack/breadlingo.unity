# BreadLingo Unity Localization

Public UPM connector for Unity's `com.unity.localization` package. I2 users should install [breadlingo.i2](https://github.com/breadpack/breadlingo.i2) instead. Unity, game assets and BreadLingo server code are not included.

## Install

In Unity Package Manager choose **Add package from git URL**:

```text
https://github.com/breadpack/breadlingo.unity.git#v0.1.0
```

For offline installation, extract the release tarball and choose **Add package from disk**, selecting `package.json`.

The initial beta targets Unity 6000.3 and pins Localization 1.4.5. Native validation used Unity 6000.3.9f1 with actual cached Localization 1.4.5 assemblies. Other combinations are unverified. Do not install alongside legacy `com.breadtranslator.unity`, which declares the same assemblies.

## Connect and synchronize

1. Set up source/target Locales and String Table Collections in Unity. Create target tables and matching entries before importing.
2. Open **Project Settings > BreadLingo**. Set `https://breadlingo.com`, workspace/project slugs, source/target locale codes and branch `main`. Source locale must exactly match the BreadLingo project.
3. Create a project Unity connector token with `unity:push`, `unity:pull`, `import:create`, `export:create`; restrict its target locales and branch. Enter it for the current Editor session or supply `BREADTRANSLATOR_TOKEN` before launching Unity. Tokens are not saved in project assets or EditorPrefs. Batchmode accepts environment credentials only.
4. Open **Window > BreadLingo > Unity Localization** and scan. **Push Source XLIFF** sends sources and existing translations in sequential chunks of 25 entries. Existing remote translations are preserved; newly imported nonempty targets require review. Changed source text invalidates approvals. Each chunk commits atomically; later failures do not roll back earlier chunks. Rescan and retry after failure.
5. Translate and approve in BreadLingo, then choose **Pull Approved for Preview**. Inspect local and approved translations and choose **Apply Reviewed Translations**. Preview does not modify assets. Local target edits during the request/review block apply. Unity Undo is available after successful apply.

Multiple target languages share collection GUID + entry ID identity. Repeat sync for each target locale; identical source uploads do not increment versions or reset approvals. Only branch `main` is supported.

Empty source strings are omitted from service uploads. Smart String flags are retained, but validation requires the same placeholder expressions; translated plural/conditional formatter bodies need separate review and are not claimed as supported in this beta.

## Preservation and file workflow

- Export XLIFF 1.2/2.0 or CSV for offline translation. XLIFF import opens the reviewed preview; CSV apply requires explicit confirmation.
- Validates existing collection GUID, entry ID, key, source text, locales and placeholders before any writes. Missing tables/entries are conflicts and never created implicitly.
- Writes target text and carries the source Smart String flag for XLIFF apply. Entry metadata, shared keys and Asset Table references are retained. Asset replacement is unsupported.
- Empty target cells preserve local translations. Delete translations explicitly in Unity.
- Verifies export checksum and typed identities. Credentials use the configured HTTPS origin with redirects disabled. Configure a trusted service origin.
- Source text in Unity is authoritative on push. Coordinate concurrent source edits through version control; independent Unity source branches are not merged. Commit/back up tables before importing.
- Pull preview is a downloaded approved snapshot, not a lock on remote review state. Pull again if review decisions change before applying.

## Optional QA and automation

Context capture, Asset Table context scanning, batch commands and consent-bound in-game QA helpers are included. Runtime/Player builds and screenshot/LQA network round trips are unverified for this beta. Never distribute project connector tokens in a shipped game; runtime integrations need an appropriate short-lived token provider.

See [installation and batch usage](Documentation~/install.md) and [validation boundaries](Documentation~/validation.md). Connector code is MIT licensed; Unity packages retain their own licenses.

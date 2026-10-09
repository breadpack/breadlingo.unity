# Validation boundaries

- Package boundaries, stable unique Unity GUIDs, license and anonymous source/release packaging.
- Actual Unity 6000.3.9f1 execution against cached Localization 1.4.5 / Addressables 1.21.19 assemblies in an isolated synthetic fixture.
- Native scanner, XLIFF 1.2/2.0, CSV, identities, locales, placeholders, preview, local edit conflicts, blank-cell preservation, Smart String and Undo tests.
- Authenticated worker HTTP with all D1 migrations in transactional SQLite: creator FK, retry/approval preservation, changed-payload rejection, source changes, current-revision export and canonical XLIFF identity.

Local Package Manager startup fails even in a clean project with `The "path" argument must be of type string. Received undefined`. Native validation therefore uses `-noUpm` and private dependency assemblies. This proves connector source execution and native table APIs, not ordinary UPM installation, Addressables/Player builds or all Unity versions. Public CI cannot replace these checks.

Production synthetic round-trip evidence and exact commits are recorded in the private release report. Direct preparation of approved fixture translations does not validate the human browser review workflow. No real game content or credentials are included in this repository or archive.

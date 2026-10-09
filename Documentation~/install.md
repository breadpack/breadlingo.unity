# Installation and automation

Install `https://github.com/breadpack/breadlingo.unity.git#v0.1.0` through Unity Package Manager. The repository is anonymous-readable; BreadLingo's private service repository is not required. Downloading the connector is free; service use requires your own project and authorized connector token.

Project manifest dependency:

```json
{"dependencies":{"com.breadpack.breadlingo.unity":"https://github.com/breadpack/breadlingo.unity.git#v0.1.0"}}
```

UPM resolves Unity Localization through the Unity registry. This package is independent of I2 and contains no I2 vendor code. Keep only one version of this connector installed.

Settings: **Project Settings > BreadLingo**. Sync: **Window > BreadLingo > Unity Localization**. API base URL is an HTTPS origin; workspace/project accept slugs. Source/target locale codes must exactly match service and Unity tables. Only `main` is supported.

Credentials use `BREADTRANSLATOR_TOKEN` (connector) and, for optional user-authorized LQA, `BREADTRANSLATOR_LQA_TOKEN`. Interactive values are memory-only and disappear after a script reload or restart. Legacy EditorPrefs values are not read and are removed when setting session values. Never commit credentials or put connector tokens in Player builds.

## Batch

Public method names retain the existing namespace:

- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.Scan`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.Validate`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.ExportXliff12`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.ExportXliff20`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.ExportCsv`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.ApplyXliff`
- `BreadTranslator.Unity.Editor.BreadTranslatorBatchCommands.ApplyCsv`

Use `-batchmode -executeMethod` with `-breadTranslatorSourceLocale`, `-breadTranslatorTargetLocale`, `-breadTranslatorOutput`, `-breadTranslatorInput` as applicable. Batch apply explicitly authorizes mutation without an interactive preview; review and commit artifacts first. Blocking conflicts return nonzero exit codes. Async commands manage termination; do not force `-quit` before network completion.

## Tests

Add `com.breadpack.breadlingo.unity` to the consuming project's `testables` and install Unity Test Framework. GitHub CI checks package boundaries and packaging only, not Unity or live service acceptance.

Official references: [Git URL installation](https://docs.unity3d.com/Manual/upm-ui-giturl.html), [Unity Localization](https://docs.unity3d.com/Packages/com.unity.localization@1.5/manual/index.html). Current upstream 1.5 documentation does not extend this beta's 1.4.5 validation to 1.5.

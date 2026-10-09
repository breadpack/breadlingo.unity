using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BreadTranslator.Unity;
using NUnit.Framework;
using UnityEngine;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class BreadTranslatorContextTests
    {
        [Test]
        public void MetadataPolicyUploadsOnlyAllowlistedSchemaKeys()
        {
            var fields = new[]
            {
                new BreadTranslatorContextField { key = "scene", value = "Assets/Scenes/Menu.unity" },
                new BreadTranslatorContextField { key = "private.designNote", value = "hidden" },
                new BreadTranslatorContextField { key = "token", value = "secret" },
                new BreadTranslatorContextField { key = "scene", value = "Assets/Scenes/Final.unity" }
            };

            var filtered = BreadTranslatorMetadataPolicy.Filter(fields, BreadTranslatorMetadataPolicy.ParseAllowlist("scene, buildVersion"));

            Assert.That(filtered, Has.Length.EqualTo(1));
            Assert.That(filtered[0].key, Is.EqualTo("scene"));
            Assert.That(filtered[0].value, Is.EqualTo("Assets/Scenes/Final.unity"));
        }

        [Test]
        public void MetadataJsonEscapesValuesAndPreservesUnityStringIdentity()
        {
            var json = BreadTranslatorApiClient.MetadataJson(
                new[] { new BreadTranslatorContextField { key = "scene", value = "Menu\n\"Final\"" } },
                new[] { new BreadTranslatorStringReference { tableCollectionGuid = "guid-1", entryId = "42", key = "menu.play" } });

            Assert.That(json, Does.Contain("Menu\\n\\\"Final\\\""));
            Assert.That(json, Does.Contain("\"tableCollectionGuid\":\"guid-1\""));
            Assert.That(json, Does.Contain("\"entryId\":\"42\""));
            Assert.That(json, Does.Not.Contain("token"));
        }

        [Test]
        public void AssetTableReferencesAreTypedDeterministicAndMetadataSafe()
        {
            var records = new[]
            {
                new AssetTableRecord("guid-z", "Icons", "Assets/Icons.asset", "20", "ui.quit", "en", "private-asset-guid-z"),
                new AssetTableRecord("guid-a", "Icons", "Assets/Icons.asset", "10", "ui.play", "en", "private-asset-guid-a")
            };
            var context = BreadTranslatorContextCapture.Collect(null, null, Array.Empty<StringTableRecord>(), records);

            Assert.That(context.candidateAssetRefs.Select(item => item.StableId), Is.EqualTo(new[]
            {
                "unity-asset:guid-a:10",
                "unity-asset:guid-z:20"
            }));
            var json = BreadTranslatorApiClient.MetadataJson(Array.Empty<BreadTranslatorContextField>(), null, context.candidateAssetRefs);
            Assert.That(json, Does.Contain("\"candidateAssetRefs\""));
            Assert.That(json, Does.Contain("\"tableCollectionGuid\":\"guid-a\""));
            Assert.That(json, Does.Contain("\"entryId\":\"10\""));
            Assert.That(json, Does.Not.Contain("private-asset-guid"));
            Assert.That(json, Does.Not.Contain("Assets/Icons.asset"));
        }

        [Test]
        public void EmptyAllowlistFailsClosed()
        {
            var filtered = BreadTranslatorMetadataPolicy.Filter(
                new[] { new BreadTranslatorContextField { key = "scene", value = "Menu" } },
                BreadTranslatorMetadataPolicy.ParseAllowlist(""));

            Assert.That(filtered, Is.Empty);
        }

        [Test]
        public void ContextCollectsHierarchyBuildAndAllowlistedMetadata()
        {
            var root = new GameObject("CanvasRoot");
            var child = new GameObject("LocalizedButton");
            child.transform.SetParent(root.transform);
            var settings = ScriptableObject.CreateInstance<BreadTranslatorProjectSettings>();
            settings.Branch = "release/1.2";
            settings.TargetLocaleCode = "ja-JP";
            settings.ContextMetadataAllowlist = "hierarchyPath,branch,locale,resolution";
            try
            {
                var context = BreadTranslatorContextCapture.Collect(settings, child, Array.Empty<StringTableRecord>());

                Assert.That(context.engine, Is.EqualTo("unity"));
                Assert.That(context.hierarchyPath, Is.EqualTo("CanvasRoot/LocalizedButton"));
                Assert.That(context.branch, Is.EqualTo("release/1.2"));
                Assert.That(context.width, Is.GreaterThan(0));
                Assert.That(context.metadata, Has.Length.EqualTo(4));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BatchJsonReportContainsOnlyRedactedOperationalFields()
        {
            var path = Path.Combine(Path.GetTempPath(), $"breadtranslator-{Guid.NewGuid():N}.json");
            try
            {
                BreadTranslatorBatchReport.Write(path, new BreadTranslatorBatchReport
                {
                    command = "report-lqa",
                    success = false,
                    exitCode = 7,
                    errorCode = "connector_access_denied",
                    httpStatus = 403,
                    context = new BreadTranslatorCaptureContext { branch = "main", locale = "ja-JP" }
                });
                var json = File.ReadAllText(path);
                Assert.That(json, Does.Contain("connector_access_denied"));
                Assert.That(json, Does.Contain("breadtranslator.connector-flow.v1"));
                Assert.That(json, Does.Contain("blocker_"));
                Assert.That(json, Does.Contain("idempotencyKeyFingerprint"));
                Assert.That(json, Does.Not.Contain("\"idempotencyKey\""));
                Assert.That(json, Does.Not.Contain("uploadUrl"));
                Assert.That(json, Does.Not.Contain("connectorToken"));
                Assert.That(json, Does.Not.Contain("authorization"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void BatchJsonReportRedactsSecretLikeMessages()
        {
            var blocker = BreadTranslatorBatchBlocker.Create("push", "remote_failed", "authorization: bearer-secret signature=abc");
            Assert.That(blocker.id, Does.StartWith("blocker_"));
            Assert.That(blocker.message, Does.Not.Contain("bearer-secret"));
            Assert.That(blocker.message, Does.Not.Contain("abc"));
        }

        [Test]
        public void BatchJsonReportRejectsRawKeyInFingerprintField()
        {
            var path = Path.Combine(Path.GetTempPath(), $"breadtranslator-{Guid.NewGuid():N}.json");
            const string rawKey = "unity-raw-key-must-never-be-reported";
            try
            {
                BreadTranslatorBatchReport.Write(path, new BreadTranslatorBatchReport
                {
                    command = "report-lqa",
                    idempotencyKeyFingerprint = rawKey
                });

                Assert.That(File.ReadAllText(path), Does.Not.Contain(rawKey));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void CancellationIsIdempotent()
        {
            var operation = BreadTranslatorApiOperation.Create("test-cancel");
            operation.Cancel();
            operation.Cancel();
            Assert.That(operation.IsCancelled, Is.True);
        }

        [Test]
        public void CallerKeyIsPreservedAndOnlyFingerprintIsPublic()
        {
            const string rawKey = "unity-retry-operation-001";
            var first = BreadTranslatorApiOperation.Create("xliff-push", rawKey);
            var replay = BreadTranslatorApiOperation.Create("xliff-push", rawKey);

            Assert.That(first.State, Is.EqualTo(BreadTranslatorApiOperationState.Active));
            Assert.That(first.IdempotencyKeyFingerprint, Is.EqualTo(replay.IdempotencyKeyFingerprint));
            Assert.That(first.IdempotencyKeyFingerprint, Does.StartWith("idemfp_"));
            Assert.That(first.IdempotencyKeyFingerprint, Does.Not.Contain(rawKey));
        }

        [Test]
        public void CancellationEndsOperationLifetimeWithoutExposingKey()
        {
            var operation = BreadTranslatorApiOperation.Create("lqa-report", "unity-cancel-operation-001");
            var fingerprint = operation.IdempotencyKeyFingerprint;

            operation.Cancel();

            Assert.That(operation.State, Is.EqualTo(BreadTranslatorApiOperationState.Cancelled));
            Assert.That(operation.IsActive, Is.False);
            Assert.That(operation.IdempotencyKeyFingerprint, Is.EqualTo(fingerprint));
        }
    }
}

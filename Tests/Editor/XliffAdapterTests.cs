using NUnit.Framework;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class XliffAdapterTests
    {
        private const string Xliff12 = "<?xml version=\"1.0\"?><xliff xmlns=\"urn:oasis:names:tc:xliff:document:1.2\" xmlns:bt=\"urn:breadtranslator:unity:1\" version=\"1.2\"><file source-language=\"en\" target-language=\"ja\"><body><trans-unit id=\"1001\" resname=\"ui.play\" bt:stable-id=\"unity:guid-ui:1001\" bt:table-guid=\"guid-ui\"><source>Play {player}</source><target>プレイ {player}</target></trans-unit></body></file></xliff>";
        private const string Xliff20 = "<?xml version=\"1.0\"?><xliff xmlns=\"urn:oasis:names:tc:xliff:document:2.0\" xmlns:bt=\"urn:breadtranslator:unity:1\" version=\"2.0\" srcLang=\"en\" trgLang=\"ja\"><file id=\"ui\"><unit id=\"1001\" name=\"ui.play\" bt:stable-id=\"unity:guid-ui:1001\" bt:table-guid=\"guid-ui\"><segment><source>Play</source><target>プレイ</target></segment></unit></file></xliff>";

        [TestCase(Xliff12, "1.2")]
        [TestCase(Xliff20, "2.0")]
        public void ParsePreservesUnityIdentityAndLocales(string input, string expectedVersion)
        {
            var parsed = XliffAdapter.Parse(input);
            Assert.That(parsed.Version, Is.EqualTo(expectedVersion));
            Assert.That(parsed.SourceLocale, Is.EqualTo("en"));
            Assert.That(parsed.TargetLocale, Is.EqualTo("ja"));
            Assert.That(parsed.Units.Count, Is.EqualTo(1));
            Assert.That(parsed.Units[0].StableId, Is.EqualTo("unity:guid-ui:1001"));
            Assert.That(parsed.Units[0].EntryId, Is.EqualTo("1001"));
            Assert.That(parsed.Units[0].Key, Is.EqualTo("ui.play"));
        }

        [Test]
        public void ParseRejectsUnitsWithoutCollectionIdentity()
        {
            const string invalid = "<xliff xmlns=\"urn:oasis:names:tc:xliff:document:1.2\" version=\"1.2\"><file source-language=\"en\" target-language=\"ja\"><body><trans-unit id=\"1\" resname=\"key\"><source>A</source><target>B</target></trans-unit></body></file></xliff>";
            Assert.Throws<System.FormatException>(() => XliffAdapter.Parse(invalid));
        }

        [TestCase("<!DOCTYPE xliff [<!ENTITY payload SYSTEM \"file:///etc/passwd\">]>")]
        [TestCase("<?xml-stylesheet href=\"https://example.test/evil.xsl\"?>")]
        public void ParseRejectsDtdEntityAndProcessingInstruction(string declaration)
        {
            var input = declaration + Xliff12.Substring(Xliff12.IndexOf("<xliff", System.StringComparison.Ordinal));
            Assert.Catch<System.Exception>(() => XliffAdapter.Parse(input));
        }

        [Test]
        public void ParseRejectsExternalReferences()
        {
            var input = Xliff12.Replace("<body>", "<body><external-file href=\"https://example.test/payload\" />");
            Assert.Throws<System.FormatException>(() => XliffAdapter.Parse(input));
        }

        [Test]
        public void ParsePreservesInlineEquivalentsAndRejectsCodeDrift()
        {
            var rich = Xliff12
                .Replace("Play {player}", "Press <x id=\"open\" equiv-text=\"&lt;b&gt;\"/>Play<x id=\"close\" equiv-text=\"&lt;/b&gt;\"/>")
                .Replace("プレイ {player}", "<x id=\"open\" equiv-text=\"&lt;b&gt;\"/>プレイ<x id=\"close\" equiv-text=\"&lt;/b&gt;\"/>");
            var parsed = XliffAdapter.Parse(rich);
            Assert.That(parsed.Units[0].SourceText, Is.EqualTo("Press <b>Play</b>"));
            Assert.That(parsed.Units[0].TargetText, Is.EqualTo("<b>プレイ</b>"));

            var mismatched = rich.Replace("id=\"close\" equiv-text=\"&lt;/b&gt;\"/></target>", "id=\"other\" equiv-text=\"&lt;/b&gt;\"/></target>");
            Assert.Throws<System.FormatException>(() => XliffAdapter.Parse(mismatched));
        }

        [Test]
        public void ExportVerificationRequiresMatchingServerChecksumAndLocales()
        {
            var result = UnityArtifactVerifier.VerifyExport(Xliff12, UnityArtifactVerifier.Sha256Hex(Xliff12), "en", "ja");
            Assert.That(result.UnitCount, Is.EqualTo(1));
            Assert.That(result.ChecksumSha256, Has.Length.EqualTo(64));
            Assert.Throws<UnityArtifactVerificationException>(() => UnityArtifactVerifier.VerifyExport(Xliff12, new string('0', 64), "en", "ja"));
            Assert.Throws<UnityArtifactVerificationException>(() => UnityArtifactVerifier.VerifyExport(Xliff12, null, "en", "ja"));
            Assert.Throws<UnityArtifactVerificationException>(() => UnityArtifactVerifier.VerifyExport(Xliff12, UnityArtifactVerifier.Sha256Hex(Xliff12), "en", "ko"));
        }

        [Test]
        public void ExportVerificationRejectsMalformedAndDuplicateIdentity()
        {
            const string malformed = "<xliff>private-translation-content";
            var malformedError = Assert.Throws<UnityArtifactVerificationException>(() =>
                UnityArtifactVerifier.VerifyExport(malformed, UnityArtifactVerifier.Sha256Hex(malformed), "en", "ja"));
            Assert.That(malformedError.Code, Is.EqualTo("xliff_malformed"));
            Assert.That(malformedError.Message, Does.Not.Contain("private-translation-content"));

            var duplicate = Xliff12.Replace("</body>", "<trans-unit id=\"1001\" resname=\"ui.play\" bt:stable-id=\"unity:guid-ui:1001\" bt:table-guid=\"guid-ui\"><source>Play {player}</source><target>Play {player}</target></trans-unit></body>");
            var duplicateError = Assert.Throws<UnityArtifactVerificationException>(() =>
                UnityArtifactVerifier.VerifyExport(duplicate, UnityArtifactVerifier.Sha256Hex(duplicate), "en", "ja"));
            Assert.That(duplicateError.Code, Is.EqualTo("xliff_identity_collision"));
        }

        [Test]
        public void ExportVerificationRejectsStableIdentityMismatch()
        {
            var mismatched = Xliff12.Replace("unity:guid-ui:1001", "unity:other-table:1001");
            var error = Assert.Throws<UnityArtifactVerificationException>(() =>
                UnityArtifactVerifier.VerifyExport(mismatched, UnityArtifactVerifier.Sha256Hex(mismatched), "en", "ja"));
            Assert.That(error.Code, Is.EqualTo("xliff_stable_id_mismatch"));
        }

        [Test]
        public void ChecksumFailureDoesNotExposeArtifactOrChecksum()
        {
            var secretChecksum = new string('f', 64);
            var error = Assert.Throws<UnityArtifactVerificationException>(() =>
                UnityArtifactVerifier.VerifyExport(Xliff12, secretChecksum, "en", "ja"));
            Assert.That(error.Message, Does.Not.Contain("Play {player}"));
            Assert.That(error.Message, Does.Not.Contain(secretChecksum));
        }
    }
}

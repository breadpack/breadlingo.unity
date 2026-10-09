using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class StringTableRecordTests
    {
        [Test]
        public void StableIdUsesCollectionGuidAndEntryId()
        {
            var record = new StringTableRecord("guid-ui", "UI", "Assets/UI.asset", "1001", "ui.play", "en", "Play", true, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            Assert.That(record.StableId, Is.EqualTo("unity:guid-ui:1001"));
        }
    }
}

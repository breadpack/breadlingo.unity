using System;
using NUnit.Framework;
using UnityEngine;
using BreadTranslator.Unity;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class BreadLingoEndpointTests
    {
        [Test]
        public void RejectsCredentialsAndForeignOrigins()
        {
            var settings = ScriptableObject.CreateInstance<BreadTranslatorProjectSettings>();
            try
            {
                settings.ApiBaseUrl = "https://breadlingo.com";
                Assert.Throws<ArgumentException>(() => BreadLingoEndpoint.Validate(settings, "https://other.example/api"));
                settings.ApiBaseUrl = "http://breadlingo.com";
                Assert.Throws<ArgumentException>(() => BreadLingoEndpoint.Build(settings, "/api/test"));
                settings.ApiBaseUrl = "https://secret@breadlingo.com";
                Assert.Throws<ArgumentException>(() => BreadLingoEndpoint.Build(settings, "/api/test"));
                settings.ApiBaseUrl = "https://breadlingo.com/path";
                Assert.Throws<ArgumentException>(() => BreadLingoEndpoint.Build(settings, "/api/test"));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
    }
}

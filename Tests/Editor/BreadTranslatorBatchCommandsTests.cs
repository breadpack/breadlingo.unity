using NUnit.Framework;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class BreadTranslatorBatchCommandsTests
    {
        [Test]
        public void BatchPushCommandContract()
        {
            Assert.That(BreadTranslatorBatchCommands.ExitCodeForApiFailure(503), Is.EqualTo(BreadTranslatorBatchCommands.HttpFailureExitCode));
            Assert.That(BreadTranslatorBatchCommands.TimeoutExitCode, Is.Not.EqualTo(BreadTranslatorBatchCommands.HttpFailureExitCode));
        }

        [Test]
        public void BatchPullApprovedApplyCommandContract()
        {
            Assert.That(BreadTranslatorBatchCommands.ExitCodeForApiFailure(409), Is.EqualTo(BreadTranslatorBatchCommands.RemoteConflictExitCode));
            Assert.That(BreadTranslatorBatchCommands.ApplyConflictExitCode, Is.Not.EqualTo(BreadTranslatorBatchCommands.RemoteConflictExitCode));
        }
    }
}

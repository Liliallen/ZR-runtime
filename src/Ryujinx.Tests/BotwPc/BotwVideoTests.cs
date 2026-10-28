using NUnit.Framework;
using Ryujinx.Common.BotwPc;
using Ryujinx.HLE.BotwPc;

namespace Ryujinx.Tests.BotwPc
{
    public class BotwVideoTests
    {
        [Test]
        public void PlaysOnlyWhileAnNvdecSessionIsOpenAndFed()
        {
            BotwVideo video = new();

            Assert.That(video.IsPlaying, Is.False);

            // Decoder work without a session (or a session before any work) is not a video.
            video.OnDecoderSubmit();
            Assert.That(video.IsPlaying, Is.False);

            VideoDecodeSessions.OnOpened();
            Assert.That(video.IsPlaying, Is.True);

            VideoDecodeSessions.OnClosed();
            Assert.That(video.IsPlaying, Is.False);

            // An extra Finalize never drives the count negative, so the next video is still detected.
            VideoDecodeSessions.OnClosed();
            VideoDecodeSessions.OnOpened();
            Assert.That(video.IsPlaying, Is.True);

            // A new game process forgets sessions left open by the previous one.
            Assert.That(new BotwVideo().IsPlaying, Is.False);
            Assert.That(VideoDecodeSessions.Open, Is.EqualTo(0));
        }
    }
}

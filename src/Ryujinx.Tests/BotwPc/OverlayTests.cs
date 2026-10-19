using NUnit.Framework;
using Ryujinx.Common.BotwPc.Overlay;
using System.Collections.Generic;

namespace Ryujinx.Tests.BotwPc
{
    public class OverlayTests
    {
        private const PadButtons LS = PadButtons.LS, RS = PadButtons.RS;

        [Test]
        public void ChordFiresAndSwallowsBothClicks()
        {
            StickChord chord = new() { WindowMs = 80 };

            Assert.That(chord.Update(LS, 0), Is.EqualTo((false, PadButtons.None)), "first click held back");
            Assert.That(chord.Update(LS | RS, 30), Is.EqualTo((true, PadButtons.None)), "second click within the window");
            Assert.That(chord.Update(LS | RS, 200).SticksForGame, Is.EqualTo(PadButtons.None), "still swallowed while held");
            Assert.That(chord.Update(RS, 220).SticksForGame, Is.EqualTo(PadButtons.None), "swallowed until both are released");
            Assert.That(chord.Update(PadButtons.None, 240).Fired, Is.False);
        }

        [Test]
        public void SimultaneousClicksFire()
        {
            StickChord chord = new();

            Assert.That(chord.Update(LS | RS, 0).Fired, Is.True);
        }

        [Test]
        public void SingleTapIsReplayedToTheGame()
        {
            StickChord chord = new() { WindowMs = 80 };

            Assert.That(chord.Update(RS, 0).SticksForGame, Is.EqualTo(PadButtons.None));
            Assert.That(chord.Update(PadButtons.None, 40), Is.EqualTo((false, RS)), "released inside the window: replayed");
            Assert.That(chord.Update(PadButtons.None, 40 + StickChord.ReplayMs - 1).SticksForGame, Is.EqualTo(RS));
            Assert.That(chord.Update(PadButtons.None, 40 + StickChord.ReplayMs + 1).SticksForGame, Is.EqualTo(PadButtons.None));
        }

        [Test]
        public void HeldClickReachesTheGameAfterTheWindow()
        {
            StickChord chord = new() { WindowMs = 80 };

            Assert.That(chord.Update(LS, 0).SticksForGame, Is.EqualTo(PadButtons.None));
            Assert.That(chord.Update(LS, 79).SticksForGame, Is.EqualTo(PadButtons.None));
            Assert.That(chord.Update(LS, 81), Is.EqualTo((false, LS)));
            Assert.That(chord.Update(LS | RS, 300).Fired, Is.False, "the other stick much later is not a chord");
        }

        [Test]
        public void NavigationRepeatsInRealTime()
        {
            NavRepeat nav = new() { DelayMs = 380, IntervalMs = 90 };
            List<double> presses = [];

            for (double t = 0; t <= 600; t += 1)
            {
                if ((nav.Update(PadButtons.Down, t) & PadButtons.Down) != 0)
                {
                    presses.Add(t);
                }
            }

            Assert.That(presses, Is.EqualTo(new double[] { 0, 380, 470, 560 }));
            Assert.That(nav.Update(PadButtons.A, 601), Is.EqualTo(PadButtons.A), "a new button is a press; no repeat for A");
        }

        [Test]
        public void MenuNavigatesSkipsHeadersAndCloses()
        {
            int value = 0;
            OverlayMenu menu = new();
            menu.Pages.Add(new OverlayPage
            {
                Title = "One",
                Items =
                [
                    new OverlayItem { Label = "H", IsHeader = true },
                    new OverlayItem { Label = "a", Value = () => value.ToString(), Change = d => value += d },
                    new OverlayItem { Label = "H2", IsHeader = true },
                    new OverlayItem { Label = "b", Enabled = () => false, Change = d => value += 100 },
                ],
            });
            menu.Pages.Add(new OverlayPage { Title = "Two", Items = [new OverlayItem { Label = "c" }] });
            menu.SetPage(0);

            Assert.That(menu.Row, Is.EqualTo(1), "first selectable row");
            menu.HandlePresses(PadButtons.Right);
            Assert.That(value, Is.EqualTo(1));
            Assert.That(menu.Changed, Is.True);
            menu.HandlePresses(PadButtons.Down);
            Assert.That(menu.Row, Is.EqualTo(3), "header skipped");
            menu.HandlePresses(PadButtons.Right);
            Assert.That(value, Is.EqualTo(1), "disabled row does not change");
            menu.HandlePresses(PadButtons.RB);
            Assert.That(menu.PageIndex, Is.EqualTo(1));
            menu.HandlePresses(PadButtons.RB);
            Assert.That(menu.PageIndex, Is.EqualTo(0), "pages wrap");
            Assert.That(menu.HandlePresses(PadButtons.B), Is.True, "B closes");
        }
    }
}

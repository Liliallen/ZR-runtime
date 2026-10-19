using System;
using System.Collections.Generic;

namespace Ryujinx.Common.BotwPc.Overlay
{
    /// <summary>When a changed setting takes effect.</summary>
    public enum ApplyKind
    {
        /// <summary>Applied immediately while the game runs.</summary>
        Live,
        /// <summary>Saved now, used from the next game start.</summary>
        GameRestart,
        /// <summary>Read-only information or an action.</summary>
        Info,
    }

    /// <summary>One row of an overlay page. Platform-neutral: the host fills in the delegates.</summary>
    public sealed class OverlayItem
    {
        public string Label { get; init; }

        /// <summary>Current value as text (null for actions and headers).</summary>
        public Func<string> Value { get; init; }

        /// <summary>Steps the value (-1 / +1, left / right). Null: not adjustable.</summary>
        public Action<int> Change { get; init; }

        /// <summary>Confirm action (A / Enter / click). Null: confirm steps the value forward.</summary>
        public Action Activate { get; init; }

        /// <summary>False: shown greyed out and cannot be changed (the note says why).</summary>
        public Func<bool> Enabled { get; init; } = () => true;

        /// <summary>Help line shown for the selected row.</summary>
        public Func<string> Note { get; init; }

        public ApplyKind Apply { get; init; } = ApplyKind.Live;

        /// <summary>A section title, not selectable.</summary>
        public bool IsHeader { get; init; }

        public bool Selectable => !IsHeader;
    }

    public sealed class OverlayPage
    {
        public string Title { get; init; }
        public List<OverlayItem> Items { get; init; } = [];
    }

    /// <summary>Navigation state of the overlay menu: pages, selected row, and input handling.</summary>
    public sealed class OverlayMenu
    {
        public List<OverlayPage> Pages { get; } = [];
        public int PageIndex { get; private set; }
        public int Row { get; private set; }

        /// <summary>Incremented on every change, so the painter knows when to redraw.</summary>
        public int Version { get; private set; }

        /// <summary>A setting was changed since the menu opened.</summary>
        public bool Changed { get; private set; }

        public OverlayPage Page => Pages[PageIndex];
        public OverlayItem Selected => Page.Items.Count > 0 ? Page.Items[Row] : null;

        public void Touch() => Version++;

        public void SetPage(int index)
        {
            PageIndex = (index % Pages.Count + Pages.Count) % Pages.Count;
            Row = FirstSelectable(PageIndex);
            Version++;
        }

        public void Select(int row)
        {
            if (row >= 0 && row < Page.Items.Count && Page.Items[row].Selectable && row != Row)
            {
                Row = row;
                Version++;
            }
        }

        private int FirstSelectable(int page)
        {
            List<OverlayItem> items = Pages[page].Items;
            int row = items.FindIndex(i => i.Selectable);

            return row < 0 ? 0 : row;
        }

        public void Move(int delta)
        {
            List<OverlayItem> items = Page.Items;

            for (int row = Row + delta; row >= 0 && row < items.Count; row += delta)
            {
                if (items[row].Selectable)
                {
                    Row = row;
                    Version++;

                    return;
                }
            }
        }

        /// <summary>Left / right on the selected row.</summary>
        public void Step(int delta)
        {
            OverlayItem item = Selected;

            if (item?.Change != null && item.Enabled())
            {
                item.Change(delta);
                Changed |= item.Apply != ApplyKind.Info;
                Version++;
            }
        }

        /// <summary>Confirm on the selected row.</summary>
        public void Confirm()
        {
            OverlayItem item = Selected;

            if (item == null || !item.Enabled())
            {
                return;
            }

            if (item.Activate != null)
            {
                item.Activate();
                Version++;
            }
            else
            {
                Step(1);
            }
        }

        /// <summary>Handles one poll of presses (physical buttons); returns true if the menu should close.</summary>
        public bool HandlePresses(PadButtons pressed)
        {
            if ((pressed & PadButtons.Up) != 0) Move(-1);
            if ((pressed & PadButtons.Down) != 0) Move(1);
            if ((pressed & PadButtons.Left) != 0) Step(-1);
            if ((pressed & PadButtons.Right) != 0) Step(1);
            if ((pressed & PadButtons.LB) != 0) SetPage(PageIndex - 1);
            if ((pressed & PadButtons.RB) != 0) SetPage(PageIndex + 1);
            if ((pressed & PadButtons.A) != 0) Confirm();

            return (pressed & (PadButtons.B | PadButtons.Menu)) != 0;
        }

        public void ResetChanged() => Changed = false;

        /// <summary>For actions that change settings outside a row (revert, defaults).</summary>
        public void MarkChanged()
        {
            Changed = true;
            Version++;
        }

        /// <summary>Cycles through a list of options (wrapping), for <see cref="OverlayItem.Change"/>.</summary>
        public static T Cycle<T>(IReadOnlyList<T> options, T current, int delta)
        {
            int index = 0;

            for (int i = 0; i < options.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(options[i], current))
                {
                    index = i;
                }
            }

            return options[((index + delta) % options.Count + options.Count) % options.Count];
        }
    }
}

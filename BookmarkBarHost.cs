// BookmarkBarHost.cs — the ★ at the right inside-edge of the URL bar.
//
// State-aware: hollow ☆ when the live page isn't saved, filled gold ★ when it
// is. Click toggles (add ⇄ remove) and persists. It refreshes whenever the URL
// box text changes — which Form1's SourceChanged already drives on every
// navigation — so no Form1 wiring is needed. The "current page" is read from
// BookmarkButtonGlue.GetCurrentPage (web.Source), not the half-typed box text,
// so typing a URL doesn't flip the star until you actually land there.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha.RingStoreCore
{
    public sealed class BookmarkBarHost
    {
        private static readonly Color StarSaved = Color.FromArgb(0xE8, 0xA3, 0x17); // gold
        private static readonly Color StarEmpty = Color.FromArgb(0x70, 0x70, 0x70); // gray

        private readonly Control _urlBox;
        private readonly BookmarkManager _mgr;
        private readonly string _storePath;
        private readonly Button _btn;
        private readonly ToolTip _tip = new ToolTip();
        private bool _attached;
        private bool _visible;
        private bool _saved;
        private Control _well;       // when set, the ★ lives in this footer well, not on the URL bar
        private bool _navWired;      // guard so the URL-change refresh hook wires only once

        /// Right inside-edge of the URL bar (true, default) vs just outside it (false).
        public bool Overlay { get; set; }
        /// Pixels of inset (overlay) or gap (outside).
        public int Gap { get; set; }

        public BookmarkBarHost(Control urlTextBox, BookmarkManager mgr, string storePath)
        {
            if (urlTextBox == null) throw new ArgumentNullException("urlTextBox");
            _urlBox = urlTextBox;
            _mgr = mgr;
            _storePath = storePath;
            Overlay = true;
            Gap = 4;

            _btn = BookmarkButtonGlue.MakeButton("\u2606"); // ☆
            _btn.Visible = false;
            _btn.Click += OnClick;
        }

        public Button Button { get { return _btn; } }
        public bool Visible { get { return _visible; } }

        /// Call once, after the form and URL box exist (end of Form1 ctor/load).
        public void Attach()
        {
            if (_attached) return;
            _well = null;               // ensure URL-bar (overlay) positioning, not footer-well
            Control parent = _urlBox.Parent;
            if (parent == null) return;

            parent.Controls.Add(_btn);
            _btn.BringToFront();
            // blend into the URL field (white) instead of reading as a gray sticker
            _btn.BackColor = _urlBox.BackColor;
            _btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_urlBox.BackColor);
            _btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_urlBox.BackColor);
            parent.SizeChanged       += delegate { Relayout(); };
            _urlBox.SizeChanged      += delegate { Relayout(); };
            _urlBox.LocationChanged  += delegate { Relayout(); };
            _urlBox.TextChanged      += delegate { RefreshState(); };
            _attached = true;
            Relayout();
            RefreshState();
        }

        /// Place the ★ INSIDE a footer well (e.g. fIndentWide) instead of overlaying
        /// the URL bar. The button reparents into the well, blends with its colour,
        /// and centres in its row; state still tracks the live page (the URL box's
        /// text changes on every navigation). Call once from the Form1 ctor before
        /// SetVisible — this marks the host attached so SetVisible won't also run the
        /// URL-bar Attach() path.
        public void PlaceIn(Control host)
        {
            if (host == null) return;
            _well = host;

            if (_btn.Parent != host)
            {
                if (_btn.Parent != null) _btn.Parent.Controls.Remove(_btn);
                host.Controls.Add(_btn);
            }
            // blend into the (sunken, light-gray) well rather than reading as a white sticker
            _btn.BackColor = host.BackColor;
            _btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(host.BackColor);
            _btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(host.BackColor);
            _btn.Font = new Font("Segoe UI Symbol", 9.75f);   // fits the slim footer row

            host.SizeChanged += delegate { Relayout(); };
            if (!_navWired) { _urlBox.TextChanged += delegate { RefreshState(); }; _navWired = true; }

            _attached = true;     // so SetVisible's `if (!_attached) Attach()` stays off the URL-bar path
            _btn.BringToFront();
            Relayout();
            RefreshState();
        }

        /// Wire to the Options "Show bookmark button" checkbox.
        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (!_attached) Attach();
            _btn.Visible = visible;
            Relayout();
            RefreshState();
        }

        /// Re-read the live page and set the star filled/hollow. Safe to call anytime.
        public void RefreshState()
        {
            bool saved = IsSaved(CurrentUrl());
            _saved = saved;
            _btn.Text = saved ? "\u2605" : "\u2606";           // ★ / ☆
            _btn.ForeColor = saved ? StarSaved : StarEmpty;
            _tip.SetToolTip(_btn, saved ? "Remove bookmark" : "Bookmark this page");
        }

        /// Force the ★ to re-position (e.g. after the form is shown and the URL
        /// bar has its final width). Safe to call anytime.
        public void Reposition() { Relayout(); }

        private void Relayout()
        {
            if (!_attached || !_btn.Visible) return;

            if (_well != null)        // footer-well placement: centre in the well, left inset
            {
                int wh = Math.Max(1, _well.ClientSize.Height);
                int h  = Math.Min(_btn.Height, wh);
                _btn.Height = h;
                _btn.Width  = Math.Min(22, Math.Max(16, _well.ClientSize.Width - 2));
                _btn.Top    = (wh - h) / 2;
                _btn.Left   = 2;
                _btn.BringToFront();
                return;
            }

            int hh = Math.Min(_btn.Width, _urlBox.Height - 2);
            _btn.Height = hh;
            _btn.Top = _urlBox.Top + (_urlBox.Height - hh) / 2;
            _btn.Left = Overlay
                ? _urlBox.Right - _btn.Width - Gap     // inside, right end
                : _urlBox.Right + Gap;                 // outside, to the right
            _btn.BringToFront();
        }

        private void OnClick(object sender, EventArgs e)
        {
            string url = CurrentUrl();
            if (string.IsNullOrEmpty(url)) return;

            if (IsSaved(url))
            {
                _mgr.Remove(url);
            }
            else
            {
                string[] p = BookmarkButtonGlue.GetCurrentPage != null
                           ? BookmarkButtonGlue.GetCurrentPage() : null;
                string title = (p != null && p.Length > 1) ? p[1] : "";
                _mgr.AddCurrentSite(url, title);
            }
            if (_mgr != null && !string.IsNullOrEmpty(_storePath)) _mgr.Save(_storePath);

            RefreshState();
            _tip.Show(_saved ? "Bookmarked" : "Removed", _btn, 0, -22, 800);
        }

        private static string CurrentUrl()
        {
            var f = BookmarkButtonGlue.GetCurrentPage;
            if (f == null) return "";
            string[] p = f();
            return (p != null && p.Length > 0 && p[0] != null) ? p[0] : "";
        }

        private bool IsSaved(string url)
        {
            if (string.IsNullOrEmpty(url) || _mgr == null || _mgr.Items == null) return false;
            foreach (Bookmark b in _mgr.Items)
                if (string.Equals(b.Url, url, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

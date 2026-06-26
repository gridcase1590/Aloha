// BookmarkButtonGlue.cs — the seam between Form1's WebView2 and the bookmark
// store, plus the ★ button factory. Form1 sets GetCurrentPage; BookmarkBarHost
// owns the button's state/toggle.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha.RingStoreCore
{
    public static class BookmarkButtonGlue
    {
        // Form1 wires this to read [url, title] of the live page (web.Source + title).
        public static Func<string[]> GetCurrentPage;

        // Add the current page and persist. Returns the bookmark, or null (no page).
        // Kept for callers that just want "add"; the bar button toggles instead.
        public static Bookmark BookmarkCurrent(BookmarkManager mgr, string storePath)
        {
            if (mgr == null) throw new ArgumentNullException("mgr");
            if (GetCurrentPage == null)
                throw new InvalidOperationException("GetCurrentPage is not wired");
            string[] p = GetCurrentPage();
            if (p == null || p.Length < 1 || string.IsNullOrEmpty(p[0])) return null;
            string url = p[0];
            string title = p.Length > 1 ? p[1] : "";
            Bookmark bm = mgr.AddCurrentSite(url, title);
            mgr.Save(storePath);
            return bm;
        }

        // Flat star that blends into the white URL field: no border, hand cursor,
        // subtle hover. BookmarkBarHost sets the glyph (★/☆) and colour by state.
        public static Button MakeButton(string text)
        {
            var b = new Button
            {
                Text = text,
                BackColor = SystemColors.Window,
                ForeColor = Color.FromArgb(0x70, 0x70, 0x70),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 11f),
                AutoSize = false,
                Width = 22,
                Height = 18,
                TabStop = false,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE8, 0xE8, 0xE8);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xD6, 0xD6, 0xD6);
            return b;
        }
    }
}

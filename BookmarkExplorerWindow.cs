using System;
using System.Drawing;
using System.Windows.Forms;
using Aloha.RingStoreCore;   // BookmarkManager

namespace Aloha
{
    // ============================================================
    // Bookmarks browser as a STANDALONE tool window (DafyFrame), like Console /
    // Live headers / Network map — its own white header + shared one-row footer
    // with indented cubes + max/close. NOT the borderless overlay the file
    // explorer uses. The pointer store is rendered in the file-tree chrome
    // (domain -> bookmark) by the embedded BookmarkTreeView, which fills the
    // ClientArea. Opened from Navigate -> Bookmarks.
    // ============================================================
    public class BookmarkExplorerWindow : DafyFrame
    {
        private readonly BookmarkTreeView view;

        public BookmarkExplorerWindow(BookmarkManager mgr, string bookmarksPath, Action<string> openCallback)
            : base("OPT-BOOK", "Bookmarks")
        {
            Size = new Size(560, 560);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(160, 160);
            ShowInTaskbar = true;
            ClientArea.BackColor = Color.Black;

            view = new BookmarkTreeView(mgr, bookmarksPath, openCallback) { Dock = DockStyle.Fill };
            // the selected bookmark's title rides in the window header; long titles scroll
            view.SelectionChanged += n =>
                SetTitle(string.IsNullOrEmpty(n) ? "Bookmarks" : "Bookmarks \u2014 " + n);
            ClientArea.Controls.Add(view);
            EnableTitleMarquee();
        }

        // host hooks (called from Form1's Navigate -> Bookmarks handler)
        public void FocusView()            { view.FocusTree(); }
        public void Reload()               { view.ReloadStore(); }
        public void NavigateTo(string url) { view.NavigateTo(url); }
    }
}

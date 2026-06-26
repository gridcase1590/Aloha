// Downloads.cs — download history for Aloha (.NET 4.8), namespace Aloha.
//
// A small JSON-persisted log of downloads, mirroring the bookmark store. Form1's
// DownloadStarting handler creates a record per download and updates it from the
// CoreWebView2DownloadOperation's BytesReceivedChanged / StateChanged events.
// Byte-progress updates raise Changed (so an open DownloadsWindow refreshes) but
// do NOT hit disk; only Add / state changes / Clear persist. JSON via the BCL
// DataContractJsonSerializer (System.Runtime.Serialization, no NuGet).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Aloha
{
    [DataContract]
    public sealed class DownloadRecord
    {
        [DataMember(Order = 0)] public string Url;
        [DataMember(Order = 1)] public string FileName;
        [DataMember(Order = 2)] public string Path;
        [DataMember(Order = 3)] public long   TotalBytes;
        [DataMember(Order = 4)] public long   ReceivedBytes;
        [DataMember(Order = 5)] public string State;        // in progress | completed | interrupted | blocked | cancelled
        [DataMember(Order = 6)] public string StartedUtc;   // ISO 8601
        [DataMember(Order = 7)] public string FinishedUtc;  // ISO 8601 ("" while running)

        private static string NowUtc()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        public static DownloadRecord Start(string url, string name, string path, long total)
        {
            return new DownloadRecord
            {
                Url = url, FileName = name, Path = path,
                TotalBytes = total, ReceivedBytes = 0,
                State = "in progress", StartedUtc = NowUtc(), FinishedUtc = ""
            };
        }

        public static DownloadRecord Blocked(string url, string name)
        {
            return new DownloadRecord
            {
                Url = url, FileName = name, Path = "",
                State = "blocked", StartedUtc = NowUtc(), FinishedUtc = NowUtc()
            };
        }

        public static DownloadRecord Cancelled(string url, string name)
        {
            return new DownloadRecord
            {
                Url = url, FileName = name, Path = "",
                State = "cancelled", StartedUtc = NowUtc(), FinishedUtc = NowUtc()
            };
        }
    }

    [DataContract]
    public sealed class DownloadFile
    {
        [DataMember(Order = 0)] public int Version = 1;
        [DataMember(Order = 1)] public List<DownloadRecord> Downloads = new List<DownloadRecord>();
    }

    public sealed class DownloadHistory
    {
        public List<DownloadRecord> Items { get; private set; }
        public event Action Changed;     // raised on add / progress / state / clear (UI refresh)

        private readonly string path;

        public DownloadHistory(string storePath)
        {
            path = storePath;
            Items = new List<DownloadRecord>();
        }

        // add + persist + notify
        public void Add(DownloadRecord r)
        {
            if (r == null) return;
            Items.Add(r);
            Save();
            RaiseChanged();
        }

        // wipe history (persist + notify)
        public void Clear()
        {
            Items.Clear();
            Save();
            RaiseChanged();
        }

        // refresh listeners WITHOUT touching disk (byte-progress ticks)
        public void RaiseChanged()
        {
            var h = Changed;
            if (h != null) h();
        }

        public void Save()
        {
            try
            {
                var file = new DownloadFile { Downloads = Items };
                var ser = new DataContractJsonSerializer(typeof(DownloadFile));
                using (var fs = File.Create(path)) ser.WriteObject(fs, file);
            }
            catch { }
        }

        public static DownloadHistory Load(string storePath)
        {
            var h = new DownloadHistory(storePath);
            if (!File.Exists(storePath)) return h;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(DownloadFile));
                using (var fs = File.OpenRead(storePath))
                {
                    var file = ser.ReadObject(fs) as DownloadFile;
                    if (file != null && file.Downloads != null) h.Items = file.Downloads;
                }
            }
            catch { }
            return h;
        }
    }
}

// RingStore.cs — seeds-only, append-only ring store for Aloha (.NET 4.8).
//
// v1 invariants, by instruction:
//   - no pointers   (only the seed citizen exists)
//   - no overwrite  (append-only; one number per position)
//
// The stored file is a 4-byte generator stamp followed by the ring numbers,
// nothing else. A ring number is self-locating: its high bits are the
// position on the ring, its low 32 bits are the payload the generator
// expands into a fragment. The hand reads by regenerating each fragment and
// placing it by position, so reconstruction is order-free and a missing
// number is a clean gap at a known position, never a desync of the tail.
//
// The bomb lives on the AUTHORED side: a fragment is whatever the generator
// makes from its payload, so a tiny store expands to a large volume. Storing
// a *pre-chosen* page is the inversion problem (the garden) and is not part
// of v1.
//
// Byte-faithful to reference_and_vectors.py: identical SHA-256 inputs give
// identical fragments. RingStoreSelfTest.Run() confirms it against verified
// vectors the first time you compile this in your solution.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Aloha.RingStoreCore
{
    public sealed class RingStore
    {
        public const int  FRAG_SIZE    = 1 << 20;       // 1 MiB per fragment
        public const int  PAYLOAD_BITS = 32;            // payload = low 32 bits
        public const uint PAYLOAD_MASK = 0xFFFFFFFFu;
        private const int STAMP_BYTES  = 4;
        private const int NUMBER_BYTES = 8;

        public uint Version { get; private set; }
        private readonly List<ulong> _numbers = new List<ulong>(); // THE stored file

        public RingStore(uint version) { Version = version; }

        /// The compressed file, in memory: just these numbers plus the stamp.
        public IList<ulong> Numbers { get { return _numbers.AsReadOnly(); } }
        public int Count { get { return _numbers.Count; } }

        // ---- write: append-only, never touches the volume -------------------
        // Authored content == GenerateFragment(payload). This method touches
        // only the number list; it has no path to the volume, so a write
        // cannot decompress. Decompress-free-on-write is structural here.
        public ulong Write(long position, uint payload)
        {
            if (position < 0) throw new ArgumentOutOfRangeException("position");
            ulong n = ((ulong)position << PAYLOAD_BITS) | payload;
            _numbers.Add(n);
            return n;
        }

        public int SerializedSize { get { return STAMP_BYTES + NUMBER_BYTES * _numbers.Count; } }

        // ---- the generator: SHA-256 keystream, version-stamped, deterministic
        public byte[] GenerateFragment(uint payload)
        {
            return GenerateFragment(payload, Version, FRAG_SIZE);
        }

        public static byte[] GenerateFragment(uint payload, uint version, int size)
        {
            // seed = UTF-8 of "version:payload"  (invariant digits, no separators)
            byte[] seed = Encoding.UTF8.GetBytes(
                version.ToString(CultureInfo.InvariantCulture) + ":" +
                payload.ToString(CultureInfo.InvariantCulture));

            byte[] outBuf = new byte[size];
            byte[] input  = new byte[seed.Length + 8];
            Buffer.BlockCopy(seed, 0, input, 0, seed.Length);

            int filled = 0;
            ulong counter = 0;
            using (SHA256 sha = SHA256.Create())
            {
                while (filled < size)
                {
                    WriteUInt64BE(input, seed.Length, counter); // block i = SHA256(seed || u64_be(i))
                    byte[] block = sha.ComputeHash(input);
                    int take = Math.Min(block.Length, size - filled);
                    Buffer.BlockCopy(block, 0, outBuf, filled, take);
                    filled += take;
                    counter++;
                }
            }
            return outBuf;
        }

        // ---- the hand: regenerate per number, place by position -------------
        // Returns null for a position no number was written to (a clean gap).
        public byte[] ReadPosition(long position)
        {
            for (int i = 0; i < _numbers.Count; i++)
            {
                ulong n = _numbers[i];
                if ((long)(n >> PAYLOAD_BITS) == position)
                    return GenerateFragment((uint)(n & PAYLOAD_MASK));
            }
            return null;
        }

        // Full reconstruction, order-free. Absent positions are simply not keyed.
        public Dictionary<long, byte[]> Hand()
        {
            var vol = new Dictionary<long, byte[]>();
            for (int i = 0; i < _numbers.Count; i++)
            {
                ulong n = _numbers[i];
                vol[(long)(n >> PAYLOAD_BITS)] = GenerateFragment((uint)(n & PAYLOAD_MASK));
            }
            return vol;
        }

        // ---- persistence: stamp + numbers, nothing else (the native store) --
        public void Save(string path)
        {
            using (FileStream fs = File.Create(path))
            {
                byte[] stamp = new byte[4];
                WriteUInt32BE(stamp, 0, Version);
                fs.Write(stamp, 0, 4);
                byte[] nb = new byte[8];
                for (int i = 0; i < _numbers.Count; i++)
                {
                    WriteUInt64BE(nb, 0, _numbers[i]);
                    fs.Write(nb, 0, 8);
                }
            }
        }

        public static RingStore Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < STAMP_BYTES) throw new InvalidDataException("store too short");
            var s = new RingStore(ReadUInt32BE(bytes, 0));
            for (int off = STAMP_BYTES; off + 8 <= bytes.Length; off += 8)
                s._numbers.Add(ReadUInt64BE(bytes, off));
            return s;
        }

        // ---- big-endian helpers (parity with python struct '>I' / '>Q') ----
        internal static void WriteUInt32BE(byte[] b, int o, uint v)
        {
            b[o]   = (byte)(v >> 24); b[o+1] = (byte)(v >> 16);
            b[o+2] = (byte)(v >> 8);  b[o+3] = (byte)v;
        }
        internal static void WriteUInt64BE(byte[] b, int o, ulong v)
        {
            for (int i = 7; i >= 0; i--) { b[o + i] = (byte)(v & 0xFF); v >>= 8; }
        }
        internal static uint ReadUInt32BE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o+1] << 16) | ((uint)b[o+2] << 8) | b[o+3];
        }
        internal static ulong ReadUInt64BE(byte[] b, int o)
        {
            ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i]; return v;
        }
    }

    // A bookmark is a run of ring numbers; its content is the fragments in
    // position order. v1 lays authored content (the generator defines the bytes).
    public struct BookmarkRef
    {
        public long Start; public int Count;
        public BookmarkRef(long start, int count) { Start = start; Count = count; }
    }

    public sealed class BookmarkStore
    {
        private readonly RingStore _ring;
        public BookmarkStore(RingStore ring) { _ring = ring; }
        public RingStore Ring { get { return _ring; } }

        // Lay an authored bookmark as consecutive positions. Append-only.
        public BookmarkRef AddAuthored(long startPosition, uint[] payloads)
        {
            for (int i = 0; i < payloads.Length; i++)
                _ring.Write(startPosition + i, payloads[i]);
            return new BookmarkRef(startPosition, payloads.Length);
        }

        // Reconstruct a bookmark's bytes. Gaps are preserved as FRAG_SIZE holes
        // (zeros) so every fragment keeps its offset — a missing fragment is a
        // hole, never a shift. For bookmarks beyond ~2 GB, stream to the
        // WebView per position instead of buffering this whole array.
        public byte[] Read(BookmarkRef bm)
        {
            long total = (long)bm.Count * RingStore.FRAG_SIZE;
            if (total > int.MaxValue)
                throw new InvalidOperationException("bookmark exceeds 2 GB; stream per-position instead");
            byte[] outBuf = new byte[(int)total];
            for (int i = 0; i < bm.Count; i++)
            {
                byte[] frag = _ring.ReadPosition(bm.Start + i);
                if (frag != null)
                    Buffer.BlockCopy(frag, 0, outBuf, i * RingStore.FRAG_SIZE, frag.Length);
                // gap -> leave zeros; offsets stay correct
            }
            return outBuf;
        }
    }
}

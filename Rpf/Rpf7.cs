// Minimal RPF7 reader/builder for DriftHub.
// Format logic ported from CodeWalker by dexyfex (RpfFile.cs, RPF7 branch):
// header (magic/entryCount/namesLen/encryption), 16-byte TOC entries,
// directory (index/count), binary bitfield (nameOff16/size24/blockOff24),
// resource (same head + sys/gfx flags), 512-byte blocks, UTF8Z names,
// Ordinal child sorting (same rule as CW saves), NG/AES TOC crypto.
// Binary files are kept decompressed and stored raw; resource files are
// copied byte-for-byte (never parsed).
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DriftHub.Rpf;

public enum RpfEncryption : uint
{
    NONE = 0,
    OPEN = 0x4E45504F,
    AES = 0x0FFFFFF9,
    NG = 0x0FEFFFFF,
}

public sealed class RpfBin
{
    public string Name = "";
    public byte[] Data = Array.Empty<byte>();    // binary: decompressed payload
    public byte[] RawDisk = Array.Empty<byte>(); // resource: verbatim on-disk bytes
    public bool IsResource;
    public uint SysFlags, GfxFlags;
}

public sealed class RpfDir
{
    public string Name = "";
    public List<RpfDir> Dirs = new();
    public List<RpfBin> Files = new();
}

public sealed class RpfArchive
{
    public string Name = ""; // file name, e.g. "dlc.rpf" (NG key derivation)
    public RpfEncryption Encryption = RpfEncryption.OPEN;
    public RpfDir Root = new();
    public Dictionary<string, RpfArchive> Nested = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- read ----------------

    public static RpfArchive Load(string path) => Load(File.ReadAllBytes(path), Path.GetFileName(path));

    public static RpfArchive Load(byte[] bytes, string name)
    {
        var arc = new RpfArchive { Name = name };
        if (bytes.Length < 16 || BitConverter.ToUInt32(bytes, 0) != 0x52504637)
            throw new InvalidDataException($"{name}: not RPF7.");
        uint entryCount = BitConverter.ToUInt32(bytes, 4);
        uint namesLen = BitConverter.ToUInt32(bytes, 8);
        arc.Encryption = (RpfEncryption)BitConverter.ToUInt32(bytes, 12);
        if ((arc.Encryption == RpfEncryption.NG && GTA5Keys.PC_NG_DECRYPT_TABLES == null)
            || (arc.Encryption == RpfEncryption.AES && GTA5Keys.PC_AES_KEY == null))
            throw new InvalidDataException(
                $"{name}: архив закрыт NG-шифрованием, ключи недоступны для этой версии GTA.");

        byte[] toc = new byte[entryCount * 16];
        Buffer.BlockCopy(bytes, 16, toc, 0, toc.Length);
        byte[] names = new byte[namesLen];
        Buffer.BlockCopy(bytes, 16 + toc.Length, names, 0, (int)Math.Min(namesLen, bytes.Length - 16 - toc.Length));
        switch (arc.Encryption)
        {
            case RpfEncryption.NONE:
            case RpfEncryption.OPEN: break;
            case RpfEncryption.AES:
                toc = GTACrypto.DecryptAES(toc);
                names = GTACrypto.DecryptAES(names);
                break;
            default:
                toc = GTACrypto.DecryptNG(toc, name, (uint)bytes.Length);
                names = GTACrypto.DecryptNG(names, name, (uint)bytes.Length);
                arc.Encryption = RpfEncryption.NG;
                break;
        }

        string GetName(uint off)
        {
            if (off >= names.Length)
                throw new InvalidDataException($"{name}: invalid name offset {off}, names length {names.Length}.");
            int end = (int)off;
            while (end < names.Length && names[end] != 0) end++;
            return Encoding.UTF8.GetString(names, (int)off, end - (int)off);
        }

        var kinds = new List<(bool isDir, bool isBin, uint w0, uint w1, uint w2, uint w3, string nm)>();
        for (int i = 0; i < entryCount; i++)
        {
            uint w0 = BitConverter.ToUInt32(toc, i * 16);
            uint w1 = BitConverter.ToUInt32(toc, i * 16 + 4);
            uint w2 = BitConverter.ToUInt32(toc, i * 16 + 8);
            uint w3 = BitConverter.ToUInt32(toc, i * 16 + 12);
            if (w1 == 0x7FFFFF00) kinds.Add((true, false, w0, w1, w2, w3, GetName(w0)));
            else if ((w1 & 0x80000000) == 0)
            {
                kinds.Add((false, true, w0, w1, w2, w3, GetName(w0 & 0xFFFF)));
            }
            else
            {
                uint no16 = (uint)(toc[i * 16] | (toc[i * 16 + 1] << 8));
                kinds.Add((false, false, w0, w1, w2, w3, GetName(no16)));
            }
        }

        RpfBin ReadBin((bool isDir, bool isBin, uint w0, uint w1, uint w2, uint w3, string nm) k)
        {
            var f = new RpfBin { Name = k.nm };
            if (!k.isBin)
            {
                f.IsResource = true;
                f.SysFlags = k.w2; f.GfxFlags = k.w3;
                // resource head: size = b2|b3<<8|b4<<16, block = b5|b6<<8|b7<<16
                int o = kinds.IndexOf(k);
                int base_ = o * 16;
                uint sz = (uint)(toc[base_ + 2] | (toc[base_ + 3] << 8) | (toc[base_ + 4] << 16));
                uint bl = (uint)(toc[base_ + 5] | (toc[base_ + 6] << 8) | (toc[base_ + 7] << 16)) & 0x7FFFFF;
                if (sz == 0xFFFFFF) throw new NotSupportedException($"{k.nm}: huge resource unsupported.");
                if (k.nm.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"{k.nm}: encrypted script unsupported.");
                f.RawDisk = new byte[sz];
                Buffer.BlockCopy(bytes, (int)(bl * 512L), f.RawDisk, 0, (int)sz);
                return f;
            }
            ulong b = k.w0 | ((ulong)k.w1 << 32);
            uint size = (uint)(b >> 16) & 0xFFFFFF;
            uint block = (uint)(b >> 40) & 0xFFFFFF;
            uint uncomp = k.w2, enc = k.w3;
            if (size == 0)
            {
                f.Data = new byte[uncomp];
                Buffer.BlockCopy(bytes, (int)(block * 512L), f.Data, 0, (int)uncomp);
            }
            else
            {
                byte[] comp = new byte[size];
                Buffer.BlockCopy(bytes, (int)(block * 512L), comp, 0, (int)size);
                if (enc == 1)
                {
                    if (arc.Encryption == RpfEncryption.AES) comp = GTACrypto.DecryptAES(comp);
                    else comp = GTACrypto.DecryptNG(comp, k.nm, uncomp);
                }
                else if (enc != 0) throw new InvalidDataException($"{k.nm}: unknown enc {enc}.");
                using var im = new MemoryStream(comp, writable: false);
                using var ds = new DeflateStream(im, CompressionMode.Decompress);
                using var om = new MemoryStream();
                ds.CopyTo(om);
                f.Data = om.ToArray();
            }
            return f;
        }

        RpfDir BuildDir(int idx)
        {
            var k = kinds[idx];
            var dir = new RpfDir { Name = idx == 0 ? "" : k.nm };
            int start = (int)k.w2, end = start + (int)k.w3;
            for (int i = start; i < end; i++)
            {
                var c = kinds[i];
                if (c.isDir) dir.Dirs.Add(BuildDir(i));
                else dir.Files.Add(ReadBin(c));
            }
            return dir;
        }
        arc.Root = BuildDir(0);

        void ScanNested(RpfDir dir, string prefix)
        {
            foreach (var f in dir.Files)
            {
                if (!f.IsResource && f.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                    && f.Data.Length >= 16 && BitConverter.ToUInt32(f.Data, 0) == 0x52504637)
                    arc.Nested[prefix + f.Name.ToLowerInvariant()] = Load(f.Data, f.Name);
            }
            foreach (var d in dir.Dirs) ScanNested(d, prefix + d.Name.ToLowerInvariant() + "/");
        }
        ScanNested(arc.Root, "");
        return arc;
    }

    // ---------------- tree helpers ----------------

    public RpfDir? FindDir(string path, bool create = false)
    {
        var cur = Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = cur.Dirs.FirstOrDefault(d => d.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
            {
                if (!create) return null;
                next = new RpfDir { Name = part.ToLowerInvariant() };
                cur.Dirs.Add(next);
            }
            cur = next;
        }
        return cur;
    }

    public static void Upsert(RpfDir dir, string name, byte[] data)
    {
        var ex = dir.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ex != null) { ex.Data = data; ex.IsResource = false; ex.RawDisk = Array.Empty<byte>(); }
        else dir.Files.Add(new RpfBin { Name = name.ToLowerInvariant(), Data = data });
    }

    public static bool Remove(RpfDir dir, string name)
    {
        int i = dir.Files.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        dir.Files.RemoveAt(i);
        return true;
    }

    public List<string> ListFiles()
    {
        var out_ = new List<string>();
        void Walk(RpfDir d, string p)
        {
            foreach (var f in d.Files) out_.Add(p + f.Name);
            foreach (var s in d.Dirs) Walk(s, p + s.Name + "/");
        }
        Walk(Root, "");
        return out_;
    }

    // ---------------- write ----------------

    public byte[] Save()
    {
        // flatten: root first, then DFS; each dir's children appended contiguously
        // at visit time and range recorded (mirrors CW EnsureAllEntries). Children
        // sorted Ordinal (dirs+files mixed) - same rule as CW saves.
        var seq = new List<(bool isDir, object node)>();
        var ranges = new Dictionary<RpfDir, (int start, int count)>();
        seq.Add((true, Root));
        var st = new Stack<RpfDir>();
        st.Push(Root);
        while (st.Count > 0)
        {
            var cur = st.Pop();
            var kids = new List<(bool isDir, object node, string nm)>();
            foreach (var d in cur.Dirs) kids.Add((true, d, d.Name));
            foreach (var f in cur.Files) kids.Add((false, f, f.Name));
            kids.Sort((a, b) => string.CompareOrdinal(a.nm, b.nm));
            int start = seq.Count;
            foreach (var k in kids) seq.Add((k.isDir, k.node));
            ranges[cur] = (start, kids.Count);
            for (int i = kids.Count - 1; i >= 0; i--)
                if (kids[i].isDir) st.Push((RpfDir)kids[i].node);
        }

        // names table (dedup, UTF8Z, pad16)
        var nameOff = new Dictionary<string, uint>();
        var nb = new List<byte>();
        uint OffOf(string n)
        {
            if (nameOff.TryGetValue(n, out var o)) return o;
            o = (uint)nb.Count;
            var b = Encoding.UTF8.GetBytes(n);
            nb.AddRange(b); nb.Add(0);
            nameOff[n] = o;
            return o;
        }
        foreach (var (isDir, node) in seq)
            OffOf(isDir ? ((RpfDir)node).Name : ((RpfBin)node).Name);
        while (nb.Count % 16 != 0) nb.Add(0);

        uint entryCount = (uint)seq.Count;
        uint namesLen = (uint)nb.Count;
        uint headerBlocks = (uint)((16 + entryCount * 16 + namesLen + 511) / 512);

        // data layout: header blocks, then files in flatten order, 512-aligned
        var blobs = new Dictionary<RpfBin, byte[]>();
        long cursor = headerBlocks * 512L;
        var filePos = new Dictionary<RpfBin, (uint block, uint len)>();
        foreach (var (isDir, node) in seq)
        {
            if (isDir) continue;
            var f = (RpfBin)node;
            byte[] payload = f.IsResource ? f.RawDisk : f.Data;
            if (payload.Length >= 0xFFFFFF) throw new NotSupportedException($"{f.Name}: too big for RPF7.");
            uint block = (uint)(cursor / 512);
            filePos[f] = (block, (uint)payload.Length);
            blobs[f] = payload;
            cursor += Align512(payload.Length);
        }
        long totalSize = cursor;

        // TOC
        using var tm = new MemoryStream();
        foreach (var (isDir, node) in seq)
        {
            if (isDir)
            {
                var d = (RpfDir)node;
                var (start, cnt) = ranges[d];
                WriteU32(tm, OffOf(d.Name));
                WriteU32(tm, 0x7FFFFF00u);
                WriteU32(tm, (uint)start);
                WriteU32(tm, (uint)cnt);
            }
            else
            {
                var f = (RpfBin)node;
                var (block, len) = filePos[f];
                if (block >= 0xFFFFFF) throw new NotSupportedException($"{f.Name}: offset too big.");
                if (!f.IsResource)
                {
                    uint no = OffOf(f.Name);
                    if (no >= 65536 || block >= 0xFFFFFF) throw new NotSupportedException($"{f.Name}: table overflow.");
                    ulong v = no | ((ulong)block << 40);
                    // FileSize field = 0 (stored raw)
                    WriteU32(tm, (uint)(v & 0xFFFFFFFF));
                    WriteU32(tm, (uint)(v >> 32));
                    WriteU32(tm, len);   // uncompressed size
                    WriteU32(tm, 0);      // EncryptionType 0
                }
                else
                {
                    uint no = OffOf(f.Name);
                    if (no >= 65536 || block >= 0x800000) throw new NotSupportedException($"{f.Name}: table overflow.");
                    tm.WriteByte((byte)(no & 0xFF));
                    tm.WriteByte((byte)((no >> 8) & 0xFF));
                    tm.WriteByte((byte)(len & 0xFF));
                    tm.WriteByte((byte)((len >> 8) & 0xFF));
                    tm.WriteByte((byte)((len >> 16) & 0xFF));
                    tm.WriteByte((byte)(block & 0xFF));
                    tm.WriteByte((byte)((block >> 8) & 0xFF));
                    tm.WriteByte((byte)(((block >> 16) & 0xFF) | 0x80)); // CW: type marker bit
                    WriteU32(tm, f.SysFlags);
                    WriteU32(tm, f.GfxFlags);
                }
            }
        }
        byte[] toc = tm.ToArray();
        byte[] names = nb.ToArray();

        var enc = Encryption;
        if (enc == RpfEncryption.NG)
        {
            toc = GTACrypto.EncryptNG(toc, Name, (uint)totalSize);
            names = GTACrypto.EncryptNG(names, Name, (uint)totalSize);
        }
        else if (enc == RpfEncryption.AES)
        {
            toc = GTACrypto.EncryptAES(toc);
            names = GTACrypto.EncryptAES(names);
        }

        using var out_ = new MemoryStream((int)Math.Min(totalSize, 256 * 1024 * 1024));
        WriteU32(out_, 0x52504637);
        WriteU32(out_, entryCount);
        WriteU32(out_, namesLen);
        WriteU32(out_, (uint)enc);
        out_.Write(toc, 0, toc.Length);
        out_.Write(names, 0, names.Length);
        while (out_.Position < headerBlocks * 512L) out_.WriteByte(0);
        foreach (var (isDir, node) in seq)
        {
            if (isDir) continue;
            var f = (RpfBin)node;
            var payload = blobs[f];
            out_.Write(payload, 0, payload.Length);
            while (out_.Position % 512 != 0) out_.WriteByte(0);
        }
        return out_.ToArray();
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 24) & 0xFF));
    }

    private static long Align512(long v) => (v + 511) & ~511L;
}

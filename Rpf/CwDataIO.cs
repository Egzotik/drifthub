// Minimal CW-compatible DataReader/DataWriter (only what CryptoIO needs).
using System.IO;

namespace DriftHub.Rpf;

public sealed class DataReader : IDisposable
{
    private readonly Stream _s;
    private readonly BinaryReader _br;
    public DataReader(Stream s) { _s = s; _br = new BinaryReader(s); }
    public byte[] ReadBytes(int n)
    {
        var b = _br.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return b;
    }
    public uint ReadUInt32() => _br.ReadUInt32();
    public void Dispose() { }
}

public sealed class DataWriter : IDisposable
{
    private readonly BinaryWriter _bw;
    public DataWriter(Stream s) { _bw = new BinaryWriter(s); }
    public void Write(byte[] v) => _bw.Write(v);
    public void Write(uint v) => _bw.Write(v);
    public void Dispose() { }
}

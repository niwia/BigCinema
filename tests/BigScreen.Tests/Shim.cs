// Pure-C# stand-ins for the four types Net/Protocol.cs needs: Vector3 and Mathf from
// UnityEngine, and Mirror's NetworkWriter/NetworkReader. The real ones live in assemblies
// that only exist next to an installed game, and the queue logic these tests care about
// (index bookkeeping, and the FIELD ORDER WriteState uses) is identical either way. Faking
// the encoding is therefore enough - and honest: it proves the order round-trips, not that
// our byte layout matches Mirror's.
//
// What the tests do NOT get is coverage of the real Protocol.cs compile. If the queue wire
// format ever changes, this file has to change with it (that is the cost of not referencing
// the game).
using System.IO;
using System.Text;

namespace BigScreen.Tests;

/// <summary>Unity's <c>Vector3</c>: three floats, nothing more.</summary>
internal struct Vector3
{
    public float x;
    public float y;
    public float z;

    public Vector3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }
}

/// <summary>Only the two <c>Mathf</c> calls Protocol.cs makes, with Unity's int overloads.</summary>
internal static class Mathf
{
    public static int Min(int a, int b) => a < b ? a : b;

    public static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}

/// <summary>A stand-in for Mirror's <c>NetworkWriter</c>: primitives into a MemoryStream.</summary>
internal sealed class NetworkWriter
{
    private readonly MemoryStream _stream = new MemoryStream();
    private readonly BinaryWriter _writer;

    public NetworkWriter()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>How many bytes the message takes, so a reader can prove it consumed them all.</summary>
    public int Length => (int)_stream.Length;

    internal void WriteInt(int v) => _writer.Write(v);

    internal void WriteFloat(float v) => _writer.Write(v);

    internal void WriteDouble(double v) => _writer.Write(v);

    internal void WriteBool(bool v) => _writer.Write(v);

    /// <summary>
    /// Mirror's null convention: a null string is written as a negative length and read back
    /// as null, never as an empty string. The call sites guard with "?? \"\"" anyway.
    /// </summary>
    internal void WriteString(string v) => _writer.Write(v);

    internal void WriteVector3(Vector3 v)
    {
        _writer.Write(v.x);
        _writer.Write(v.y);
        _writer.Write(v.z);
    }

    public byte[] ToArray()
    {
        _writer.Flush();
        return _stream.ToArray();
    }
}

/// <summary>A stand-in for Mirror's <c>NetworkReader</c>: primitives back out of a byte[].</summary>
internal sealed class NetworkReader
{
    private readonly MemoryStream _stream;
    private readonly BinaryReader _reader;

    public NetworkReader(byte[] bytes)
    {
        _stream = new MemoryStream(bytes, writable: false);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Bytes nobody read. Must be zero after a ReadState - a non-zero number means the
    /// writer and the reader disagree about the field order/layout, which is exactly the
    /// failure that would silently corrupt the messages after ours on a real session.
    /// </summary>
    public int Remaining => (int)(_stream.Length - _stream.Position);

    internal int ReadInt() => _reader.ReadInt32();

    internal float ReadFloat() => _reader.ReadSingle();

    internal double ReadDouble() => _reader.ReadDouble();

    internal bool ReadBool() => _reader.ReadBoolean();

    internal string ReadString() => _reader.ReadString();

    internal Vector3 ReadVector3() =>
        new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
}

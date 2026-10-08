namespace ChatLab.Shared;

/// <summary>Byte counters for one file transfer, one slot per parallel stream. Written by workers, polled by the UI.</summary>
public sealed class TransferProgress
{
    public const int DefaultStreams = 4;

    public long Size { get; }
    public List<(long Offset, long Length)> Parts { get; }
    public long[] Done { get; }

    public TransferProgress(long size, int maxStreams = DefaultStreams)
    {
        Size = size;
        Parts = Wire.Split(size, maxStreams);
        Done = new long[Parts.Count];
    }

    public void Add(int part, long bytes) => Interlocked.Add(ref Done[part], bytes);

    public long TotalDone
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < Done.Length; i++) sum += Interlocked.Read(ref Done[i]);
            return sum;
        }
    }
}

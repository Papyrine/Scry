using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Scry;

/// <summary>
/// A write-ahead file in front of a disclosure sink. An answer is accepted once its record is on
/// this machine's disk, and the sink behind is handed that record afterwards, in order, by a
/// background shipper — so a store that is slow, or briefly down, costs a caller nothing and loses
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// What "accepted" is worth here is a flushed local file. That survives the process crashing and the
/// machine restarting. It does <b>not</b> survive the disk being lost before the shipper caught up,
/// so a container needs a persistent volume under the directory, and a deployment that must survive
/// the loss of a node accepts into something shared instead.
/// </para>
/// <para>
/// Until a record is shipped its content lies in the directory in the clear, so the directory needs
/// the protection the store behind it has. One process per directory: a second one opening it is
/// refused.
/// </para>
/// <para>
/// Delivery to the sink behind is at least once — a crash between handing a record over and noting
/// that it was is indistinguishable, afterwards, from a crash before — which is why every sink keeps
/// a batch once however often it is handed it.
/// </para>
/// </remarks>
public sealed class ScryDisclosureJournal :
    IScryDisclosureSink,
    IScryDisclosureStatus,
    IDisposable,
    IAsyncDisposable
{
    // "SDJ1", then four bytes kept for later and the position the segment starts at.
    const uint segmentMagic = 0x314A4453;
    const int segmentHeader = 16;

    // "SDJG", the payload's length, how many records it holds, and when it was accepted.
    const uint groupMarker = 0x474A4453;
    const int groupHeader = 20;
    const int checksumLength = 8;

    // A group is one write and one flush for every answer that was waiting. Bounded so that a burst
    // of large answers is several flushes rather than one frame the size of all of them.
    const int groupLimit = 4 * 1024 * 1024;

    const string segmentExtension = ".sdj";

    sealed record Pending(PooledBufferWriter Record, TaskCompletionSource Accepted);

    sealed class Segment(long start, string path)
    {
        public long Start => start;
        public string Path => path;

        // The bytes of groups it holds: its file's length, less the header.
        public long Length;

        public long End => start + Length;
    }

    // A plain object rather than a Lock: the writer waits on it for something to write.
    object gate = new();
    string directory;
    IScryDisclosureSink inner;
    ScryDisclosureJournalOptions options;
    FileStream lockFile;
    List<Segment> segments = [];
    FileStream active = null!;
    List<Pending> pending = [];
    long queued;

    // Positions count the bytes of groups from the first ever written, across segments. Everything
    // below `written` is flushed; everything below `shipped` the sink behind has accepted.
    long written;
    long shipped;
    long writtenRecords;
    long shippedRecords;
    Exception? failure;
    bool closing;
    bool disposed;
    Thread writer;
    Task shipper;
    SemaphoreSlim arrived = new(0);
    CancelSource stopping = new();
    List<(long Records, TaskCompletionSource Reached)> waiting = [];

    // Whether the sink behind was made for this journal, and so is this journal's to close. One a
    // host handed over is the host's.
    internal bool OwnsInner { get; init; }

    /// <summary>Opens the journal in <paramref name="directory"/>, in front of <paramref name="inner"/>.</summary>
    /// <param name="directory">
    /// Where the journal keeps its files. Created where it does not exist. Whatever an earlier process
    /// left here unshipped is shipped first.
    /// </param>
    /// <param name="inner">The sink records are shipped to.</param>
    /// <param name="options">How large the journal may grow, and how it paces itself.</param>
    /// <exception cref="ScryDisclosureException">
    /// The directory is held by another process, or what it holds is damaged somewhere a crash could
    /// not have damaged it.
    /// </exception>
    public ScryDisclosureJournal(string directory, IScryDisclosureSink inner, ScryDisclosureJournalOptions? options = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.inner = inner;
        this.options = options ?? new();
        Validate(this.options);
        Directory.CreateDirectory(this.directory);
        lockFile = Hold(this.directory);
        try
        {
            Recover();
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }

        QueryRecorder.JournalBytes(written - shipped);
        writer = new(Writing)
        {
            IsBackground = true,
            Name = "Scry disclosure journal"
        };
        writer.Start();
        shipper = Task.Run(Ship);
    }

    static void Validate(ScryDisclosureJournalOptions options)
    {
        if (options.MaxBytes < 1)
        {
            throw new ArgumentException($"{nameof(ScryDisclosureJournalOptions)}.{nameof(options.MaxBytes)} must be greater than zero. It is the most the journal may hold that its sink has not accepted.");
        }

        if (options.SegmentBytes < 1)
        {
            throw new ArgumentException($"{nameof(ScryDisclosureJournalOptions)}.{nameof(options.SegmentBytes)} must be greater than zero. It is how large a segment file grows before the next is started.");
        }

        if (options.RetryDelay <= TimeSpan.Zero ||
            options.MaxRetryDelay < options.RetryDelay)
        {
            throw new ArgumentException($"{nameof(ScryDisclosureJournalOptions)}.{nameof(options.RetryDelay)} must be greater than zero, and {nameof(options.MaxRetryDelay)} at least as long. They are how long the shipper waits before asking a sink that refused again.");
        }
    }

    // Held for as long as the journal is open. Two processes appending to one directory would each
    // write over the other's records, so the second is told rather than let in.
    static FileStream Hold(string directory)
    {
        try
        {
            return new(Path.Combine(directory, "journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new ScryDisclosureException($"The disclosure journal at '{directory}' is held by another process. A journal directory belongs to one process: give each node a directory of its own.", exception);
        }
    }

    /// <inheritdoc />
    public void Append(ScryDisclosureBatch batch) =>
        Enqueue(batch).GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        cancel.ThrowIfCancellationRequested();

        // The wait is what a caller going away abandons. The record is written all the same, which
        // errs the way everything here does: towards a record of something that did not go.
        return new(Enqueue(batch).WaitAsync(cancel));
    }

    Task Enqueue(ScryDisclosureBatch batch)
    {
        var record = new PooledBufferWriter();
        try
        {
            batch.Serialize(record);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (failure is not null)
                {
                    throw Broken(failure);
                }

                if (closing)
                {
                    throw new ScryDisclosureException("The disclosure journal is closed, so the record of this answer could not be kept.");
                }

                // Counted as it will lie on disk, less the few bytes of framing its group adds.
                var size = sizeof(int) + record.WrittenCount;
                var held = written - shipped + queued;
                if (held + size > options.MaxBytes)
                {
                    throw new ScryDisclosureException($"The disclosure journal holds {held} bytes its store has not accepted, and may hold {options.MaxBytes}. The store behind it is not keeping up or cannot be reached.");
                }

                queued += size;
                pending.Add(new(record, accepted));
                Monitor.Pulse(gate);
            }

            return accepted.Task;
        }
        catch
        {
            record.Dispose();
            throw;
        }
    }

    // Once a write or a flush has failed, what reached the disk is not known, and saying "accepted"
    // again on the strength of a later flush that happened to succeed would be saying it about records
    // that may not be there. So the journal stays broken until the process starts again and reads back
    // what is really on disk.
    static ScryDisclosureException Broken(Exception exception) =>
        new($"The disclosure journal failed and accepts nothing until the process restarts: {exception.Message}", exception);

    // The one writer. Everything waiting when it looks is written as one group and flushed once, so
    // answers arriving together share a flush — and a group is on disk before the next is begun, which
    // is what lets recovery tell a write the crash cut short from damage.
    void Writing()
    {
        var group = new List<Pending>();
        try
        {
            while (true)
            {
                lock (gate)
                {
                    while (pending.Count == 0 &&
                           !closing)
                    {
                        Monitor.Wait(gate);
                    }

                    if (pending.Count == 0)
                    {
                        return;
                    }

                    Gather(group);
                }

                var length = Commit(group);
                lock (gate)
                {
                    written += length;
                    writtenRecords += group.Count;
                    queued -= group.Sum(_ => sizeof(int) + _.Record.WrittenCount);
                    segments[^1].Length += length;
                }

                QueryRecorder.JournalBytes(length);
                Settle(group, failed: null);
                arrived.Release();
            }
        }
        catch (Exception exception)
        {
            List<Pending> stranded;
            lock (gate)
            {
                failure ??= exception;
                stranded = [.. group, .. pending];
                pending.Clear();
            }

            Settle(stranded, exception);
        }
    }

    // Under the gate. As many of the waiting records as make one group of a sensible size, in the
    // order they arrived, and always at least one.
    void Gather(List<Pending> group)
    {
        var size = 0;
        var count = 0;
        while (count < pending.Count &&
               (count == 0 || size + pending[count].Record.WrittenCount <= groupLimit))
        {
            size += sizeof(int) + pending[count].Record.WrittenCount;
            count++;
        }

        group.AddRange(pending.Take(count));
        pending.RemoveRange(0, count);
    }

    static void Settle(List<Pending> group, Exception? failed)
    {
        foreach (var entry in group)
        {
            entry.Record.Dispose();
            if (failed is null)
            {
                entry.Accepted.TrySetResult();
            }
            else
            {
                entry.Accepted.TrySetException(Broken(failed));
            }
        }

        group.Clear();
    }

    // Frames a group, writes it and flushes it. Returns how many bytes it came to.
    int Commit(List<Pending> group)
    {
        bool full;
        lock (gate)
        {
            full = segments[^1].Length >= options.SegmentBytes;
        }

        if (full)
        {
            Roll();
        }

        var payload = group.Sum(_ => sizeof(int) + _.Record.WrittenCount);
        var length = groupHeader + payload + checksumLength;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var span = frame.AsSpan(0, length);
            BinaryPrimitives.WriteUInt32LittleEndian(span, groupMarker);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], payload);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], group.Count);
            BinaryPrimitives.WriteInt64LittleEndian(span[12..], options.Clock.GetUtcNow().UtcTicks);
            var offset = groupHeader;
            foreach (var entry in group)
            {
                var record = entry.Record.WrittenMemory.Span;
                BinaryPrimitives.WriteInt32LittleEndian(span[offset..], record.Length);
                record.CopyTo(span[(offset + sizeof(int))..]);
                offset += sizeof(int) + record.Length;
            }

            Checksum(span[..offset], span[offset..]);
            active.Write(span);

            // To the disk, not to the operating system's cache: this is the line an answer waits at.
            active.Flush(flushToDisk: true);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    // Eight bytes of SHA-256: enough to tell a group that was written whole from one that was not,
    // from a hash already in the framework rather than a package brought in for a faster one.
    static void Checksum(ReadOnlySpan<byte> framed, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(framed, hash);
        hash[..checksumLength].CopyTo(destination);
    }

    // Every group in the segment being left was flushed as it was written, so there is nothing to
    // flush here but the new file's own beginning.
    void Roll()
    {
        active.Dispose();
        long start;
        lock (gate)
        {
            start = written;
        }

        var segment = Create(start);
        lock (gate)
        {
            segments.Add(segment);
        }
    }

    Segment Create(long start)
    {
        var segment = new Segment(start, Path.Combine(directory, $"{start:x16}{segmentExtension}"));
        active = new(segment.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, bufferSize: 1);
        Span<byte> header = stackalloc byte[segmentHeader];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, segmentMagic);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], start);
        active.Write(header);
        active.Flush(flushToDisk: true);
        SyncDirectory();
        return segment;
    }

    // A new file's name is the directory's to keep, and a filesystem may keep names apart from
    // contents, so that a file flushed whole is still missing after a crash. Windows has no such
    // flush to ask for and does not need one. Elsewhere it is asked for where the runtime will open a
    // directory at all; where it will not, what is relied on is what the common filesystems do in
    // practice, which is to carry a new file's name along with its first flush.
    void SyncDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var handle = File.OpenHandle(directory);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Reads back what an earlier process left: where the journal ends, and how far its sink had got.
    void Recover()
    {
        var found = Directory.GetFiles(directory, $"*{segmentExtension}")
            .Select(path => (Path: path, Start: Start(path)))
            .OrderBy(_ => _.Start)
            .ToList();
        for (var index = 0; index < found.Count; index++)
        {
            var (path, start) = found[index];
            var last = index == found.Count - 1;
            var segment = new Segment(start, path)
            {
                Length = Scan(path, start, last, out var records)
            };
            writtenRecords += records;
            if (!last &&
                segment.End != found[index + 1].Start)
            {
                throw Damaged($"segment '{Path.GetFileName(path)}' ends at {segment.End} and the next begins at {found[index + 1].Start}, so records between them are missing");
            }

            segments.Add(segment);
        }

        var checkpoint = ReadCheckpoint();
        if (segments.Count == 0)
        {
            // Nothing left unshipped. Positions carry on from where the last process stopped, so a
            // checkpoint that outlived its segments still describes this journal.
            written = checkpoint ?? 0;
            shipped = written;
            segments.Add(Create(written));
            writtenRecords = 0;
            return;
        }

        written = segments[^1].End;
        active = new(segments[^1].Path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete, bufferSize: 1);
        active.Seek(0, SeekOrigin.End);
        shipped = Resume(checkpoint);
        shippedRecords = Count(segments[0].Start, shipped);
    }

    static long Start(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 16 &&
            long.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var start))
        {
            return start;
        }

        throw Damaged($"'{Path.GetFileName(path)}' is not a segment this journal wrote");
    }

    // Where shipping takes up: the checkpoint, where it names somewhere in what is still here. One
    // that points before the first segment is from before those records were shipped and removed, and
    // one that is missing was never written — either way everything here is shipped again, which
    // costs a sink that keeps each batch once nothing.
    long Resume(long? checkpoint)
    {
        var first = segments[0].Start;
        if (checkpoint is not { } position ||
            position < first)
        {
            return first;
        }

        if (position > written)
        {
            throw Damaged($"its checkpoint is at {position} and its records end at {written}, so segments are missing");
        }

        // A checkpoint is only ever written at the end of a group. One that is not there was not
        // written by this code, and the segment it lies in is shipped again from its beginning.
        var segment = segments.Last(_ => _.Start <= position);
        if (Boundary(segment, position))
        {
            return position;
        }

        return segment.Start;
    }

    /// <summary>
    /// Walks a segment's groups, checking each. Returns how many bytes of whole groups it holds.
    /// </summary>
    /// <remarks>
    /// A group is flushed before the next is begun, so the only group a crash can cut short is the
    /// last one of the last segment — and nothing in such a group was acknowledged, since
    /// acknowledging is what follows the flush. That one is dropped. A group that fails anywhere else,
    /// with a whole group after it, was whole once and has been damaged since: that is not something
    /// to repair by forgetting records that were acknowledged, so the journal refuses to open.
    /// </remarks>
    long Scan(string path, long start, bool last, out long records)
    {
        records = 0;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        var size = RandomAccess.GetLength(handle);
        Span<byte> header = stackalloc byte[segmentHeader];
        if (size < segmentHeader ||
            Fill(handle, header, 0) != segmentHeader ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != segmentMagic ||
            BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != start)
        {
            // A segment whose own beginning never reached the disk holds nothing, and can only be
            // the newest one.
            if (last &&
                size <= segmentHeader)
            {
                Rewrite(handle, start);
                return 0;
            }

            throw Damaged($"segment '{Path.GetFileName(path)}' does not begin as a segment does");
        }

        long offset = segmentHeader;
        while (offset < size)
        {
            if (Group(handle, offset, size) is not var (length, count))
            {
                if (last &&
                    !AnyGroupAfter(handle, offset + 1, size))
                {
                    RandomAccess.SetLength(handle, offset);
                    RandomAccess.FlushToDisk(handle);
                    break;
                }

                throw Damaged($"segment '{Path.GetFileName(path)}' is damaged at byte {offset}, before records that are intact. Those records were accepted and may not have reached the store: move the segment aside to start without it, and keep it");
            }

            offset += length;
            records += count;
        }

        return offset - segmentHeader;
    }

    static void Rewrite(SafeFileHandle handle, long start)
    {
        Span<byte> header = stackalloc byte[segmentHeader];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, segmentMagic);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], start);
        RandomAccess.SetLength(handle, 0);
        RandomAccess.Write(handle, header, 0);
        RandomAccess.FlushToDisk(handle);
    }

    // The whole group at an offset: its length on disk and how many records it holds, or null where
    // what is there is not a whole group.
    static (int Length, int Records)? Group(SafeFileHandle handle, long offset, long size)
    {
        Span<byte> header = stackalloc byte[groupHeader];
        if (size - offset < groupHeader + checksumLength ||
            Fill(handle, header, offset) != groupHeader ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != groupMarker)
        {
            return null;
        }

        var payload = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var records = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (payload < 0 ||
            records < 0 ||
            payload > size - offset - groupHeader - checksumLength)
        {
            return null;
        }

        var length = groupHeader + payload + checksumLength;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var span = frame.AsSpan(0, length);
            if (Fill(handle, span, offset) != length)
            {
                return null;
            }

            Span<byte> expected = stackalloc byte[checksumLength];
            Checksum(span[..^checksumLength], expected);
            if (!expected.SequenceEqual(span[^checksumLength..]))
            {
                return null;
            }

            return (length, records);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    // Whether a whole group begins anywhere from an offset on. Looked for by its marker, since the
    // length that would have led to it is part of what cannot be trusted.
    static bool AnyGroupAfter(SafeFileHandle handle, long offset, long size)
    {
        const int window = 64 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            Span<byte> marker = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(marker, groupMarker);
            var position = offset;
            while (position < size)
            {
                var read = Fill(handle, buffer.AsSpan(0, window), position);
                if (read < marker.Length)
                {
                    return false;
                }

                var searched = buffer.AsSpan(0, read);
                var from = 0;
                while (searched[from..].IndexOf(marker) is >= 0 and var found)
                {
                    if (Group(handle, position + from + found, size) is not null)
                    {
                        return true;
                    }

                    from += found + 1;
                }

                // Stepped back by less than a marker, so one lying across two reads is still met.
                position += read - (marker.Length - 1);
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Reads until the span is full or the file ends, since one read may hand back less than was
    // asked for. Returns how much was read.
    static int Fill(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    // Whether a position is where a group of the segment ends, or where its first begins.
    static bool Boundary(Segment segment, long position)
    {
        using var handle = File.OpenHandle(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = segmentHeader + segment.Length;
        long offset = segmentHeader;
        while (segment.Start + offset - segmentHeader < position)
        {
            if (Group(handle, offset, size) is not var (length, _))
            {
                return false;
            }

            offset += length;
        }

        return segment.Start + offset - segmentHeader == position;
    }

    // How many records lie between two positions that are both ends of groups.
    long Count(long from, long to)
    {
        long records = 0;
        foreach (var segment in segments)
        {
            if (segment.End <= from ||
                segment.Start >= to)
            {
                continue;
            }

            using var handle = File.OpenHandle(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = segmentHeader + segment.Length;
            long offset = segmentHeader;
            while (offset < size)
            {
                var position = segment.Start + offset - segmentHeader;
                if (position >= to ||
                    Group(handle, offset, size) is not var (length, count))
                {
                    break;
                }

                if (position >= from)
                {
                    records += count;
                }

                offset += length;
            }
        }

        return records;
    }

    static ScryDisclosureException Damaged(string what) =>
        new($"The disclosure journal cannot be opened: {what}.");

    // Hands the sink behind every group in order, taking up where the last process left off. A sink
    // that refuses is asked again, for as long as it takes: what the journal has accepted it owes.
    async Task Ship()
    {
        var stop = stopping.Token;
        var delay = options.RetryDelay;
        try
        {
            while (true)
            {
                long from;
                long to;
                lock (gate)
                {
                    from = shipped;
                    to = written;
                }

                if (from == to)
                {
                    await arrived.WaitAsync(stop);
                    continue;
                }

                var (batches, length) = Read(from);
                try
                {
                    foreach (var batch in batches)
                    {
                        await inner.AppendAsync(batch, stop);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The whole group again, from its first record: a sink keeps a batch once.
                    QueryRecorder.JournalRefused(exception);
                    await Task.Delay(delay, options.Clock, stop);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxRetryDelay.Ticks));
                    continue;
                }

                delay = options.RetryDelay;
                Advance(from + length, batches.Count);
                QueryRecorder.JournalBytes(-length);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Not the sink's doing: the journal's own files could not be read back. Nothing more can
            // be shipped, so nothing more is accepted either.
            List<Pending> stranded;
            lock (gate)
            {
                failure ??= exception;
                stranded = [.. pending];
                pending.Clear();
            }

            Settle(stranded, exception);
            Reached();
        }
    }

    void Advance(long position, int records)
    {
        List<Segment> finished;
        lock (gate)
        {
            shipped = position;
            shippedRecords += records;

            // A segment wholly shipped has nothing left to say. The newest is the one being written.
            finished = [.. segments.Take(segments.Count - 1).Where(_ => _.End <= position)];
            segments.RemoveAll(finished.Contains);
        }

        // Before the segments it has outlived are removed, so the checkpoint never names somewhere
        // that is gone. Not flushed: one that is lost costs shipping again, which costs nothing.
        WriteCheckpoint(position);
        foreach (var segment in finished)
        {
            File.Delete(segment.Path);
        }

        Reached();
    }

    // The group at a position, as the batches it holds and its length on disk.
    (List<ScryDisclosureBatch> Batches, int Length) Read(long position)
    {
        Segment segment;
        lock (gate)
        {
            segment = segments.Last(_ => _.Start <= position);
        }

        using var handle = File.OpenHandle(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = segmentHeader + position - segment.Start;
        Span<byte> header = stackalloc byte[groupHeader];
        if (Fill(handle, header, offset) != groupHeader ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != groupMarker)
        {
            throw new InvalidDataException($"The disclosure journal's segment '{Path.GetFileName(segment.Path)}' no longer holds a group at byte {offset}.");
        }

        var payload = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var length = groupHeader + payload + checksumLength;
        var frame = new byte[length];
        Span<byte> expected = stackalloc byte[checksumLength];
        if (Fill(handle, frame, offset) != length)
        {
            throw new InvalidDataException($"The disclosure journal's segment '{Path.GetFileName(segment.Path)}' ends inside the group at byte {offset}.");
        }

        Checksum(frame.AsSpan(0, length - checksumLength), expected);
        if (!expected.SequenceEqual(frame.AsSpan(length - checksumLength)))
        {
            throw new InvalidDataException($"The disclosure journal's segment '{Path.GetFileName(segment.Path)}' is damaged at byte {offset}.");
        }

        var batches = new List<ScryDisclosureBatch>(count);
        var at = groupHeader;
        for (var index = 0; index < count; index++)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(at));
            batches.Add(ScryDisclosureBatch.Deserialize(frame.AsSpan(at + sizeof(int), size)));
            at += sizeof(int) + size;
        }

        return (batches, length);
    }

    string CheckpointPath => Path.Combine(directory, "checkpoint");

    // Written beside and moved over, so a reader finds the old one or the new one and never half of
    // either.
    void WriteCheckpoint(long position)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) + checksumLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, position);
        Checksum(bytes[..sizeof(long)], bytes[sizeof(long)..]);
        var beside = CheckpointPath + ".tmp";
        File.WriteAllBytes(beside, bytes);
        File.Move(beside, CheckpointPath, overwrite: true);
    }

    long? ReadCheckpoint()
    {
        if (!File.Exists(CheckpointPath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(CheckpointPath);
        Span<byte> expected = stackalloc byte[checksumLength];
        if (bytes.Length != sizeof(long) + checksumLength)
        {
            return null;
        }

        Checksum(bytes.AsSpan(0, sizeof(long)), expected);
        if (!expected.SequenceEqual(bytes.AsSpan(sizeof(long))))
        {
            return null;
        }

        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    /// <summary>
    /// Completes once everything accepted before the call has been accepted by the sink behind, or
    /// fails where the journal broke before it could be.
    /// </summary>
    public Task DrainAsync(Cancel cancel = default)
    {
        TaskCompletionSource reached;
        lock (gate)
        {
            if (failure is not null)
            {
                throw Broken(failure);
            }

            // Counted in records rather than bytes: what is still waiting to be written has no
            // position yet, and how many records there are is exact either way.
            var wanted = writtenRecords + pending.Count;
            if (shippedRecords >= wanted)
            {
                return Task.CompletedTask;
            }

            reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((wanted, reached));
        }

        return reached.Task.WaitAsync(cancel);
    }

    // Lets go of everyone waiting for the shipper to have got as far as it now has.
    void Reached()
    {
        List<TaskCompletionSource> done = [];
        Exception? broken;
        lock (gate)
        {
            broken = failure;
            for (var index = waiting.Count - 1; index >= 0; index--)
            {
                if (broken is null &&
                    waiting[index].Records > shippedRecords)
                {
                    continue;
                }

                done.Add(waiting[index].Reached);
                waiting.RemoveAt(index);
            }
        }

        foreach (var reached in done)
        {
            if (broken is null)
            {
                reached.TrySetResult();
            }
            else
            {
                reached.TrySetException(Broken(broken));
            }
        }
    }

    /// <summary>
    /// What the journal holds that the sink behind has not accepted, added to what that sink says of
    /// itself where it says anything.
    /// </summary>
    public async ValueTask<ScryDisclosureStoreStatus> Status(Cancel cancel = default)
    {
        long records;
        long bytes;
        long from;
        lock (gate)
        {
            records = writtenRecords - shippedRecords;
            bytes = written - shipped;
            from = shipped;
        }

        DateTimeOffset? oldest = null;
        if (bytes > 0)
        {
            oldest = Accepted(from);
        }

        if (inner is not IScryDisclosureStatus behind)
        {
            return new(records, bytes, oldest, Events: 0);
        }

        var status = await behind.Status(cancel);
        return status with
        {
            Pending = status.Pending + records,
            PendingBytes = status.PendingBytes + bytes,
            OldestPending = Earlier(status.OldestPending, oldest)
        };
    }

    static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null ||
            right < left)
        {
            return right;
        }

        return left;
    }

    // When the group at a position was accepted, or null where the shipper has just moved past it.
    DateTimeOffset? Accepted(long position)
    {
        Segment? segment;
        lock (gate)
        {
            segment = segments.LastOrDefault(_ => _.Start <= position);
        }

        if (segment is null)
        {
            return null;
        }

        try
        {
            using var handle = File.OpenHandle(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[groupHeader];
            if (Fill(handle, header, segmentHeader + position - segment.Start) != groupHeader ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != groupMarker)
            {
                return null;
            }

            return new(BinaryPrimitives.ReadInt64LittleEndian(header[12..]), TimeSpan.Zero);
        }
        catch (IOException)
        {
            // Shipped and removed between being named and being opened.
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Stops accepting, writes what was waiting, and gives the shipper
    /// <see cref="ScryDisclosureJournalOptions.DrainTimeout"/> to hand the sink behind what is left.
    /// Whatever it does not get to stays in the directory, and is shipped when the journal is next
    /// opened.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            closing = true;
            Monitor.PulseAll(gate);
        }

        writer.Join();
        try
        {
            await DrainAsync().WaitAsync(options.DrainTimeout);
        }
        catch (Exception exception) when (exception is TimeoutException or ScryDisclosureException)
        {
            // Left for the next process to ship.
        }

        await stopping.CancelAsync();
        await shipper;
        long held;
        lock (gate)
        {
            held = written - shipped;
        }

        // What this journal no longer speaks for: the next to open the directory counts it afresh.
        QueryRecorder.JournalBytes(-held);
        await active.DisposeAsync();
        await lockFile.DisposeAsync();
        stopping.Dispose();
        arrived.Dispose();
        if (!OwnsInner)
        {
            return;
        }

        if (inner is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync();
        }
        else if (inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Stops at once, as a process that was killed would: nothing more is written, nothing is drained,
    /// and the files are left exactly as they lie. What a test of recovery starts from.
    /// </summary>
    internal async Task Abandon()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            closing = true;
            failure ??= new ObjectDisposedException(nameof(ScryDisclosureJournal));
            Monitor.PulseAll(gate);
        }

        await stopping.CancelAsync();
        await shipper;
        writer.Join();
        long held;
        lock (gate)
        {
            held = written - shipped;
        }

        QueryRecorder.JournalBytes(-held);
        await active.DisposeAsync();
        await lockFile.DisposeAsync();
        stopping.Dispose();
        arrived.Dispose();
    }
}

/// <summary>How a <see cref="ScryDisclosureJournal"/> bounds and paces itself.</summary>
// begin-snippet: disclosureJournalOptions
public sealed class ScryDisclosureJournalOptions
{
    /// <summary>
    /// The most the journal may hold that the sink behind it has not accepted, in bytes. Default
    /// 1,073,741,824 (1 GiB). Past it nothing more is accepted, so answers fail rather than pile up
    /// on a disk that will fill: a store that has been unreachable that long is an outage to be told
    /// about, not one to be absorbed.
    /// </summary>
    public long MaxBytes { get; set; } = 1L << 30;

    /// <summary>
    /// How large a segment file grows before the next is started, in bytes. Default 8,388,608
    /// (8 MiB). A segment is removed once everything in it has been shipped, so this is also about
    /// how much disk a journal that is keeping up occupies.
    /// </summary>
    public int SegmentBytes { get; set; } = 8 << 20;

    /// <summary>
    /// How long the shipper waits before asking again a sink that refused. Default one second,
    /// doubling with each refusal up to <see cref="MaxRetryDelay"/>.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest the shipper waits between attempts. Default thirty seconds.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long closing the journal waits for the sink behind to accept what is left. Default five
    /// seconds. What is not accepted by then stays on disk for the next process to ship.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The clock groups are timed by, and the shipper waits by. The system's by default.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}
// end-snippet

using System.Buffers;

namespace Serilog.Viewer.Infrastructure.Reading;

/// <summary>
/// Forward reader that splits a log file into lines inside one pooled buffer.
/// <para>
/// Lines are handed out as ranges of <see cref="Buffer"/> rather than as strings or copies, so a
/// caller that only needs a few fields of each line never materialises the line itself. A range
/// stays valid until the next <see cref="FillAsync"/> or <see cref="Seek"/> call.
/// </para>
/// <para>
/// Usage: drain <see cref="TryReadLine"/>, call <see cref="FillAsync"/>, repeat until it returns
/// false; whatever is left is an unterminated final line, available from
/// <see cref="TryReadRemainder"/>.
/// </para>
/// </summary>
internal sealed class LogLineReader : IAsyncDisposable
{
    private const int InitialBufferSize = 256 * 1024;

    /// <summary>
    /// A "line" longer than this is handed out in pieces instead of growing the buffer further, so
    /// a file with no line breaks at all cannot pull itself wholly into memory.
    /// </summary>
    private const int MaxLineBytes = 32 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly long _limit;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private long _bufferOffset;
    private bool _exhausted;

    private LogLineReader(FileStream stream, long startOffset, long limit)
    {
        _stream = stream;
        _limit = limit;
        _bufferOffset = startOffset;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    /// <summary>
    /// Opens <paramref name="path"/> positioned at <paramref name="startOffset"/>. Bytes at or
    /// beyond <paramref name="limit"/> are never read, which lets a caller pin a read to the file
    /// size it sampled earlier while the file keeps growing.
    /// </summary>
    public static LogLineReader Open(
        string path,
        long startOffset = 0,
        long limit = long.MaxValue,
        bool sequential = true
    )
    {
        var options = FileOptions.Asynchronous;
        if (sequential)
            options |= FileOptions.SequentialScan;

        // bufferSize 1 turns off FileStream's own buffer: every read here is already a large
        // block into our buffer, so a second copy through FileStream's would be pure overhead.
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            options
        );

        try
        {
            if (startOffset > 0)
                stream.Seek(startOffset, SeekOrigin.Begin);
            return new LogLineReader(stream, startOffset, limit);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public byte[] Buffer => _buffer;

    /// <summary>File offset of the next unread byte: the end of the last line handed out.</summary>
    public long Position => _bufferOffset + _start;

    public long FileLength => _stream.Length;

    /// <summary>
    /// Takes the next newline-terminated line from the buffered data. The range excludes the line
    /// break (LF or CRLF) and a UTF-8 byte order mark at the start of the file. Returns false when
    /// the buffer holds no complete line; call <see cref="FillAsync"/> and try again.
    /// </summary>
    public bool TryReadLine(out int start, out int length, out long offset)
    {
        var available = _end - _start;
        var newline = available > 0 ? _buffer.AsSpan(_start, available).IndexOf((byte)'\n') : -1;

        int consumed;
        if (newline >= 0)
        {
            length = newline;
            consumed = newline + 1;
        }
        else if (available >= MaxLineBytes)
        {
            length = available;
            consumed = available;
        }
        else
        {
            start = 0;
            length = 0;
            offset = 0;
            return false;
        }

        start = _start;
        offset = _bufferOffset + _start;
        _start += consumed;

        Trim(ref start, ref length, offset);
        return true;
    }

    /// <summary>
    /// After <see cref="FillAsync"/> has returned false: the unterminated text left at the end of
    /// the range, if any. For a file that is still being written this may be half a line.
    /// </summary>
    public bool TryReadRemainder(out int start, out int length, out long offset)
    {
        if (_start >= _end)
        {
            start = 0;
            length = 0;
            offset = 0;
            return false;
        }

        start = _start;
        length = _end - _start;
        offset = _bufferOffset + _start;
        _start = _end;

        Trim(ref start, ref length, offset);
        return true;
    }

    /// <summary>
    /// Reads more of the file into the buffer, keeping any unread bytes. Returns false once the
    /// end of the file or the read limit has been reached.
    /// </summary>
    public async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_exhausted)
            return false;

        if (_start > 0)
        {
            var unread = _end - _start;
            if (unread > 0)
                System.Buffer.BlockCopy(_buffer, _start, _buffer, 0, unread);
            _bufferOffset += _start;
            _start = 0;
            _end = unread;
        }

        if (_end == _buffer.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            System.Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        var wanted = (int)Math.Min(_buffer.Length - _end, _limit - (_bufferOffset + _end));
        if (wanted <= 0)
        {
            _exhausted = true;
            return false;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end, wanted), cancellationToken);
        if (read == 0)
        {
            _exhausted = true;
            return false;
        }

        _end += read;
        return true;
    }

    /// <summary>
    /// Repositions the reader. A target that is already buffered costs nothing, which is what
    /// makes visiting many nearby offsets cheap; anything else discards the buffer and seeks.
    /// </summary>
    public void Seek(long offset)
    {
        var relative = offset - _bufferOffset;
        if (relative >= 0 && relative <= _end)
        {
            _start = (int)relative;
            return;
        }

        _stream.Seek(offset, SeekOrigin.Begin);
        _bufferOffset = offset;
        _start = 0;
        _end = 0;
        _exhausted = false;
    }

    private void Trim(ref int start, ref int length, long offset)
    {
        if (length > 0 && _buffer[start + length - 1] == (byte)'\r')
            length--;

        if (
            offset == 0
            && length >= 3
            && _buffer[start] == 0xEF
            && _buffer[start + 1] == 0xBB
            && _buffer[start + 2] == 0xBF
        )
        {
            start += 3;
            length -= 3;
        }
    }

    /// <summary>True when the range holds nothing but ASCII white space.</summary>
    public static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != 0x0B && b != 0x0C)
                return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var buffer = Interlocked.Exchange(ref _buffer, []);
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);

        await _stream.DisposeAsync();
    }
}

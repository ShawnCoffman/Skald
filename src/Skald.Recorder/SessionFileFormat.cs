using System.Buffers;
using System.IO.Compression;

namespace Skald.Recorder;

// Recordings and journals are Brotli. Consecutive samples are nearly identical and Brotli's window spans many of them, which makes
// files roughly seven times smaller than gzip. Files written by earlier versions are gzip and are still read.
internal static class SessionFileFormat
{
    private const int Quality = 5;

    public static Stream Compress(Stream output, bool leaveOpen = false)
        => new BrotliStream(output, new BrotliCompressionOptions { Quality = Quality }, leaveOpen);

    // Brotli input ends at the first corrupt or missing byte instead of throwing, so a journal whose tail was torn by a power loss
    // still yields every entry flushed before it. Gzip input (earlier versions) throws InvalidDataException there, as it always did.
    public static Stream Decompress(Stream input)
    {
        Span<byte> magic = stackalloc byte[2];
        var read = input.ReadAtLeast(magic, 2, throwOnEndOfStream: false);
        input.Position = 0;
        return read == 2 && magic[0] == 0x1f && magic[1] == 0x8b
            ? new GZipStream(input, CompressionMode.Decompress)
            : new LenientBrotliStream(input);
    }

    // BrotliStream throws away everything decoded in the Read call that runs into invalid data. The decoder also swallows all the
    // input it is given before emitting output, and signals NeedMoreData while output is still pending, so this feeds it a small
    // slice at a time and drains fully between slices. Corruption then costs at most the entry sharing its slice.
    private sealed class LenientBrotliStream(Stream input) : Stream
    {
        private const int Slice = 64;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private BrotliDecoder _decoder;
        private int _start;
        private int _end;
        private int _fed;
        private bool _finished;

        public override int Read(Span<byte> destination)
        {
            var total = 0;
            while (!_finished && destination.Length > 0)
            {
                var status = _decoder.Decompress(_buffer.AsSpan(_start, _fed), destination, out var consumed, out var written);
                _start += consumed;
                _fed -= consumed;
                total += written;
                destination = destination[written..];
                switch (status)
                {
                    case OperationStatus.Done:
                    case OperationStatus.InvalidData:
                        _finished = true;
                        break;
                    case OperationStatus.DestinationTooSmall:
                        if (written == 0) return total;
                        break;
                    case OperationStatus.NeedMoreData:
                        // Output is still draining, or part of the slice is left: call again with the same slice.
                        if (written > 0 || _fed > 0) break;
                        if (_start == _end)
                        {
                            _start = _end = 0;
                            var read = input.Read(_buffer, 0, _buffer.Length);
                            if (read <= 0)
                            {
                                _finished = true;
                                break;
                            }
                            _end = read;
                        }
                        _fed = Math.Min(Slice, _end - _start);
                        break;
                }
            }
            return total;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _decoder.Dispose();
                input.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

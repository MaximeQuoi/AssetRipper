namespace AssetRipper.IO.Files.Streams;

/// <summary>
/// Read a slice of a file with System.IO.RandomAccess.
/// This allows a container file with multiple logical files (eg, AssetBundles)
/// to be streamed as if they are separate files, without buffering the entire file at once.
/// </summary>
/// <remarks>
/// The file is either an open <see cref="FileStream"/> or a <see cref="PooledFile"/>,
/// whose operating system handle is only kept open while the pool has room for it.
/// </remarks>
internal sealed class RandomAccessStream : Stream
{
	private const int BufferSize = 4096;

	public RandomAccessSource Source { get; }

	public long BaseOffset { get; }

	/// <summary>
	/// Raw position in file.
	/// </summary>
	private long position = 0;

	/// <summary>
	/// Small reads (headers, metadata) are served from this buffer, filled with one read of the file.
	/// </summary>
	private byte[]? buffer;
	private long bufferStart;
	private int bufferLength;

	public override bool CanRead => true;

	public override bool CanSeek => true;

	public override bool CanWrite => false;

	public override long Length { get; }

	// position corrected for logical file offset.
	public override long Position
	{
		get => position - BaseOffset;
		set => position = value + BaseOffset;
	}

	public RandomAccessStream(FileStream parent, long offset, long length) : this(new FileStreamSource(parent), offset, length)
	{
	}

	public RandomAccessStream(RandomAccessSource source, long offset, long length)
	{
		if (source.Length < offset + length)
		{
			throw new ArgumentException("The parent stream is not long enough for the given offset and length.");
		}
		Source = source;
		BaseOffset = offset;
		Length = length;
		position = BaseOffset;
	}

	public override void Flush()
	{
		// Read-only streams shouldn't flush.
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> destination)
	{
		long toRead = Math.Max(Math.Min(destination.Length, Length - Position), 0);
		if (toRead == 0)
		{
			return 0;
		}
		destination = destination[..(int)toRead];

		if (destination.Length < BufferSize)
		{
			if (buffer is null || position < bufferStart || position + destination.Length > bufferStart + bufferLength)
			{
				FillBuffer();
			}
			int available = (int)Math.Min(destination.Length, bufferStart + bufferLength - position);
			if (available > 0)
			{
				buffer.AsSpan((int)(position - bufferStart), available).CopyTo(destination);
				position += available;
				return available;
			}
			return 0;
		}

		int read = Source.Read(destination, position);
		position += read;
		return read;
	}

	[MemberNotNull(nameof(buffer))]
	private void FillBuffer()
	{
		buffer ??= new byte[BufferSize];
		long end = BaseOffset + Length;
		int size = (int)Math.Min(BufferSize, end - position);
		bufferStart = position;
		bufferLength = size > 0 ? Source.Read(buffer.AsSpan(0, size), position) : 0;
	}

	public override int ReadByte()
	{
		Span<byte> result = stackalloc byte[1];
		return Read(result) == 1 ? result[0] : -1;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		switch (origin)
		{
			case SeekOrigin.Current:
				Position += offset;
				break;
			case SeekOrigin.Begin:
				Position = offset;
				break;
			case SeekOrigin.End:
				position = (Length + offset) + BaseOffset;
				break;
		}
		return Position;
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}

	protected override void Dispose(bool disposing)
	{
		buffer = null;
		base.Dispose(disposing);
	}
}

/// <summary>
/// A file read at given offsets.
/// </summary>
internal abstract class RandomAccessSource
{
	public abstract long Length { get; }

	public abstract int Read(Span<byte> destination, long fileOffset);
}

internal sealed class FileStreamSource(FileStream stream) : RandomAccessSource
{
	public FileStream Stream { get; } = stream;

	public override long Length => Stream.Length;

	public override int Read(Span<byte> destination, long fileOffset)
	{
		return RandomAccess.Read(Stream.SafeFileHandle, destination, fileOffset);
	}
}

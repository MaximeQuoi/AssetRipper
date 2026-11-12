using Microsoft.Win32.SafeHandles;

namespace AssetRipper.IO.Files.Streams;

/// <summary>
/// A file on disk read through a shared pool of operating system handles.
/// </summary>
/// <remarks>
/// A game can have tens of thousands of asset bundles, and the data of an uncompressed bundle is read lazily
/// from its file until the export ends. Keeping one handle per file exceeds the open file limit of Linux and macOS
/// ("Too many open files"). The pool keeps at most <see cref="MaxOpenHandles"/> handles open and closes
/// the least recently used one when it needs room; a closed file is reopened on its next read.
/// </remarks>
internal sealed class PooledFile : RandomAccessSource
{
	/// <summary>
	/// The number of handles kept open by the pool, well below the usual limits (1024 on Linux, 256 on macOS by default).
	/// </summary>
	public const int MaxOpenHandles = 128;

	private static readonly Lock poolLock = new();
	private static readonly LinkedList<PooledFile> openFiles = new();

	private readonly string path;
	private SafeFileHandle? handle;
	private LinkedListNode<PooledFile>? node;
	private int readers;

	public override long Length { get; }

	public PooledFile(string path)
	{
		this.path = Path.GetFullPath(path);
		using (SafeFileHandle firstHandle = File.OpenHandle(this.path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Length = RandomAccess.GetLength(firstHandle);
		}
	}

	public override int Read(Span<byte> destination, long fileOffset)
	{
		SafeFileHandle fileHandle = Acquire();
		try
		{
			return RandomAccess.Read(fileHandle, destination, fileOffset);
		}
		finally
		{
			Release();
		}
	}

	private SafeFileHandle Acquire()
	{
		lock (poolLock)
		{
			if (handle is null)
			{
				EvictIfFull();
				handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				node = openFiles.AddFirst(this);
			}
			else if (node is not null && node != openFiles.First)
			{
				openFiles.Remove(node);
				openFiles.AddFirst(node);
			}
			readers++;
			return handle;
		}
	}

	private void Release()
	{
		lock (poolLock)
		{
			readers--;
		}
	}

	/// <summary>
	/// Closes the least recently used handles that are not being read, so that a new one fits in the pool.
	/// </summary>
	private static void EvictIfFull()
	{
		LinkedListNode<PooledFile>? candidate = openFiles.Last;
		while (openFiles.Count >= MaxOpenHandles && candidate is not null)
		{
			LinkedListNode<PooledFile>? previous = candidate.Previous;
			PooledFile file = candidate.Value;
			if (file.readers == 0)
			{
				file.CloseHandle();
			}
			candidate = previous;
		}
	}

	private void CloseHandle()
	{
		if (node is not null)
		{
			openFiles.Remove(node);
			node = null;
		}
		handle?.Dispose();
		handle = null;
	}

	/// <summary>
	/// Closes the handle, when no stream reads the file any more.
	/// </summary>
	public void Close()
	{
		lock (poolLock)
		{
			if (readers == 0)
			{
				CloseHandle();
			}
		}
	}

	/// <summary>
	/// Opens a file of the local disk for reading through the pool.
	/// </summary>
	public static Stream OpenRead(string path)
	{
		PooledFile file = new(path);
		return new PooledFileStream(file);
	}

	/// <summary>
	/// A whole pooled file, which releases its handle when disposed.
	/// </summary>
	private sealed class PooledFileStream(PooledFile file) : Stream
	{
		private readonly RandomAccessStream inner = new(file, 0, file.Length);

		public PooledFile File { get; } = file;

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
		public override int Read(Span<byte> buffer) => inner.Read(buffer);
		public override int ReadByte() => inner.ReadByte();
		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
				File.Close();
			}
			base.Dispose(disposing);
		}
	}

	/// <summary>
	/// The pooled file behind a stream returned by <see cref="OpenRead(string)"/>, if any.
	/// </summary>
	public static PooledFile? GetFile(Stream stream) => (stream as PooledFileStream)?.File;
}

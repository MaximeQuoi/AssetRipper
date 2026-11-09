using System.Runtime.InteropServices;

namespace AssetRipper.GUI.Web;

/// <summary>
/// Raises the open file limit (RLIMIT_NOFILE) on Linux and macOS.
/// </summary>
/// <remarks>
/// Large games open many files while loading. The JIT runtime raises the soft limit to the hard limit at startup,
/// but NativeAOT builds keep the default soft limit, often 1024 on Linux, which fails with "Too many open files".
/// </remarks>
internal static class FileDescriptorLimit
{
	[StructLayout(LayoutKind.Sequential)]
	private struct RLimit
	{
		public ulong Current;
		public ulong Maximum;
	}

	/// <summary>
	/// RLIM_INFINITY: all bits set on Linux, 2^63 - 1 on macOS.
	/// </summary>
	private static bool IsInfinity(ulong value) => value >= long.MaxValue;
	/// <summary>
	/// Maximum number of files per process accepted by setrlimit on macOS (OPEN_MAX).
	/// </summary>
	private const ulong MacOSOpenMax = 10240;

	[DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
	private static extern int GetRLimit(int resource, out RLimit limit);

	[DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
	private static extern int SetRLimit(int resource, ref RLimit limit);

	/// <summary>
	/// Raises the soft limit as high as allowed.
	/// </summary>
	/// <returns>A description of the limit, for the log, or null on Windows or failure.</returns>
	public static string? Raise()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			return null;
		}
		int resource = OperatingSystem.IsMacOS() ? 8 : 7; // RLIMIT_NOFILE
		try
		{
			if (GetRLimit(resource, out RLimit limit) != 0)
			{
				return null;
			}
			ulong before = limit.Current;
			ulong target = limit.Maximum;
			if (OperatingSystem.IsMacOS() || IsInfinity(target))
			{
				target = System.Math.Min(target, OperatingSystem.IsMacOS() ? MacOSOpenMax : 1_048_576);
			}
			if (target > limit.Current)
			{
				RLimit raised = new() { Current = target, Maximum = limit.Maximum };
				if (SetRLimit(resource, ref raised) != 0 && OperatingSystem.IsMacOS() && target > MacOSOpenMax)
				{
					raised.Current = MacOSOpenMax;
					SetRLimit(resource, ref raised);
				}
				GetRLimit(resource, out limit);
			}
			return $"Open file limit: {Format(limit.Current)} (was {Format(before)}, hard limit {Format(limit.Maximum)})";
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}

		static string Format(ulong value) => IsInfinity(value) ? "unlimited" : value.ToString();
	}
}

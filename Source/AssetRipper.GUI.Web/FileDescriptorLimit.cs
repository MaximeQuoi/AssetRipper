using System.Runtime.InteropServices;

namespace AssetRipper.GUI.Web;

/// <summary>
/// Raises the open file limit (RLIMIT_NOFILE) on Linux and macOS as high as the system allows.
/// </summary>
/// <remarks>
/// Large games open many files while loading. The JIT runtime raises the soft limit to the hard limit at startup,
/// but NativeAOT builds keep the default soft limit, often 1024 on Linux and 256 on macOS, which fails with
/// "Too many open files". Game files are read through a pool of handles, so the limit only needs some room,
/// but it is raised to the maximum anyway.
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
	/// The limit below which a warning is logged.
	/// </summary>
	private const ulong RecommendedLimit = 4096;

	/// <summary>
	/// RLIM_INFINITY: all bits set on Linux, 2^63 - 1 on macOS.
	/// </summary>
	private static bool IsInfinity(ulong value) => value >= long.MaxValue;

	/// <summary>
	/// Maximum number of files per process accepted by setrlimit on older macOS versions (OPEN_MAX).
	/// </summary>
	private const ulong MacOSOpenMax = 10240;

	/// <summary>
	/// Default of /proc/sys/fs/nr_open, the kernel maximum of RLIMIT_NOFILE on Linux.
	/// </summary>
	private const ulong LinuxDefaultNrOpen = 1_048_576;

	// glibc's libc.so is a linker script that dlopen cannot load, so libc.so.6 is tried first on Linux.
	[DllImport("libc.so.6", EntryPoint = "getrlimit", SetLastError = true)]
	private static extern int GetRLimitGlibc(int resource, out RLimit limit);

	[DllImport("libc.so.6", EntryPoint = "setrlimit", SetLastError = true)]
	private static extern int SetRLimitGlibc(int resource, ref RLimit limit);

	[DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
	private static extern int GetRLimitLibc(int resource, out RLimit limit);

	[DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
	private static extern int SetRLimitLibc(int resource, ref RLimit limit);

	[DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
	private static extern int SysctlByName(string name, out int value, ref nint length, nint newValue, nint newLength);

	private static bool useGlibc = OperatingSystem.IsLinux();

	private static int GetRLimit(int resource, out RLimit limit)
	{
		if (useGlibc)
		{
			try
			{
				return GetRLimitGlibc(resource, out limit);
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				useGlibc = false; // musl and others
			}
		}
		return GetRLimitLibc(resource, out limit);
	}

	private static int SetRLimit(int resource, ref RLimit limit)
	{
		return useGlibc ? SetRLimitGlibc(resource, ref limit) : SetRLimitLibc(resource, ref limit);
	}

	/// <summary>
	/// Raises the soft limit (and the hard limit, when allowed) as high as possible.
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
				return $"Open file limit: could not be read (error {Marshal.GetLastPInvokeError()})";
			}
			ulong before = limit.Current;
			ulong systemMaximum = GetSystemMaximum();

			// The hard limit can only be raised by a privileged process (root, CAP_SYS_RESOURCE): try, then fall back.
			if (!IsInfinity(limit.Maximum) && limit.Maximum < systemMaximum)
			{
				TrySet(resource, systemMaximum, systemMaximum);
				GetRLimit(resource, out limit);
			}

			ulong target = IsInfinity(limit.Maximum) ? systemMaximum : System.Math.Min(limit.Maximum, systemMaximum);
			if (target > limit.Current)
			{
				// Try the highest value first, then lower ones (macOS may refuse more than OPEN_MAX).
				foreach (ulong candidate in new[] { target, target / 2, target / 4, MacOSOpenMax, RecommendedLimit })
				{
					if (candidate <= limit.Current || candidate > target)
					{
						continue;
					}
					if (TrySet(resource, candidate, limit.Maximum))
					{
						break;
					}
				}
				GetRLimit(resource, out limit);
			}

			string message = $"Open file limit: {Format(limit.Current)} (was {Format(before)}, hard limit {Format(limit.Maximum)})";
			if (limit.Current < RecommendedLimit)
			{
				message += OperatingSystem.IsLinux()
					? $". The hard limit is low: raise it with 'ulimit -Hn {systemMaximum}' as root or in /etc/security/limits.conf if large games fail with \"Too many open files\""
					: ". Raise it with 'ulimit -n' if large games fail with \"Too many open files\"";
			}
			return message;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return $"Open file limit: could not be raised ({ex.GetType().Name})";
		}

		static string Format(ulong value) => IsInfinity(value) ? "unlimited" : value.ToString();
	}

	private static bool TrySet(int resource, ulong current, ulong maximum)
	{
		RLimit raised = new() { Current = current, Maximum = maximum };
		return SetRLimit(resource, ref raised) == 0;
	}

	/// <summary>
	/// The largest limit the kernel accepts for one process.
	/// </summary>
	private static ulong GetSystemMaximum()
	{
		if (OperatingSystem.IsLinux())
		{
			try
			{
				string text = File.ReadAllText("/proc/sys/fs/nr_open").Trim();
				if (ulong.TryParse(text, out ulong nrOpen) && nrOpen > 0)
				{
					return nrOpen;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
			return LinuxDefaultNrOpen;
		}
		else
		{
			try
			{
				nint length = sizeof(int);
				if (SysctlByName("kern.maxfilesperproc", out int maxFilesPerProc, ref length, 0, 0) == 0 && maxFilesPerProc > 0)
				{
					return (ulong)maxFilesPerProc;
				}
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
			}
			return MacOSOpenMax;
		}
	}
}

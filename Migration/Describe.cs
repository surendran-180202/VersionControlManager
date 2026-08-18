namespace VersionControlManager.Migration;

/// <summary>Human-readable sizes for the progress output.</summary>
internal static class Describe
{
	#region Fields
	private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];
	#endregion

	#region Publics
	public static string Bytes(long value)
	{
		if(value <= 0)
		{
			return "0 bytes";
		}

		double size = value;
		int unit = 0;

		while(size >= 1024 && unit < Units.Length - 1)
		{
			size /= 1024;
			unit++;
		}

		return unit == 0 ? $"{value:N0} bytes" : $"{size:N1} {Units[unit]}";
	}

	/// <summary>GitHub reports repository size in kilobytes.</summary>
	public static string Kilobytes(long value)
	{
		return value <= 0 ? "unknown" : Bytes(value * 1024);
	}
	#endregion
}

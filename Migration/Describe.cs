namespace VersionControlManager.Migration;

/// <summary>Human-readable sizes for the progress output.</summary>
internal static class Describe
{
	#region Fields
	private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];
	#endregion

	#region Publics
	public static string Bytes(long lValue)
	{
		if(lValue <= 0) return "0 bytes";

		double size = lValue;
		int nUnit = 0;

		while(size >= 1024 && nUnit < Units.Length - 1)
		{
			size /= 1024;
			nUnit++;
		}

		return nUnit == 0 ? $"{lValue:N0} bytes" : $"{size:N1} {Units[nUnit]}";
	}

	/// <summary>GitHub reports repository size in kilobytes.</summary>
	public static string Kilobytes(long lValue)
	{
		return lValue <= 0 ? "unknown" : Bytes(lValue * 1024);
	}
	#endregion
}

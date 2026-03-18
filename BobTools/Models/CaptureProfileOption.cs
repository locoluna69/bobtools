namespace BobTools.Models;

public sealed record CaptureProfileOption(string Value, string DisplayName)
{
	public override string ToString() => DisplayName;
}
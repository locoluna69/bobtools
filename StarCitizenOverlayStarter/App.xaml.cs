namespace StarCitizenOverlayStarter;

public partial class App : System.Windows.Application
{
	public static string SessionId { get; } = Guid.NewGuid().ToString();
}
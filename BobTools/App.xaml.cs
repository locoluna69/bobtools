using System.IO;
using System.Text;
using System.Windows.Threading;

namespace BobTools;

public partial class App : System.Windows.Application
{
	public static string SessionId { get; } = Guid.NewGuid().ToString();

	protected override void OnStartup(System.Windows.StartupEventArgs e)
	{
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		base.OnStartup(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		WriteUnhandledExceptionLog("DispatcherUnhandledException", e.Exception);
	}

	private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
	{
		WriteUnhandledExceptionLog("AppDomainUnhandledException", e.ExceptionObject as Exception, e.ExceptionObject);
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		WriteUnhandledExceptionLog("TaskSchedulerUnobservedTaskException", e.Exception);
	}

	private static void WriteUnhandledExceptionLog(string source, Exception? exception, object? rawExceptionObject = null)
	{
		try
		{
			var logDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"BobTools",
				"logs");

			Directory.CreateDirectory(logDirectory);
			var logPath = Path.Combine(logDirectory, $"startup-error-{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");

			var builder = new StringBuilder();
			builder.AppendLine("BobTools unhandled exception");
			builder.AppendLine($"TimestampLocal: {DateTime.Now:O}");
			builder.AppendLine($"SessionId: {SessionId}");
			builder.AppendLine($"Source: {source}");
			builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
			builder.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
			builder.AppendLine();

			if (exception is not null)
			{
				builder.AppendLine(exception.ToString());
			}
			else if (rawExceptionObject is not null)
			{
				builder.AppendLine(rawExceptionObject.ToString());
			}
			else
			{
				builder.AppendLine("No exception details were available.");
			}

			File.WriteAllText(logPath, builder.ToString());
		}
		catch
		{
			// Never throw from the exception logger.
		}
	}
}
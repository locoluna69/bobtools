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

		LoadEncryptedEnv();

		base.OnStartup(e);
	}

	private static void LoadEncryptedEnv()
	{
		var envPath = Path.Combine(AppContext.BaseDirectory, ".env.enc");
		if (!File.Exists(envPath)) return;

		try
		{
			var bytes = File.ReadAllBytes(envPath);
			var key = Encoding.UTF8.GetBytes("BobToolsKey12345"); // 16 bytes
			var iv = Encoding.UTF8.GetBytes("BobToolsIV123456"); // 16 bytes

			using var aes = System.Security.Cryptography.Aes.Create();
			aes.Key = key;
			aes.IV = iv;

			using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
			using var ms = new MemoryStream(bytes);
			using var cs = new System.Security.Cryptography.CryptoStream(ms, decryptor, System.Security.Cryptography.CryptoStreamMode.Read);
			using var sr = new StreamReader(cs);

			var content = sr.ReadToEnd();
			foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Split('=', 2);
				if (parts.Length == 2)
				{
					Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
				}
			}
		}
		catch (Exception ex)
		{
			WriteUnhandledExceptionLog("LoadEncryptedEnv", ex);
		}
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
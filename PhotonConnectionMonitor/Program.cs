using NLog;
using System.Threading.Tasks;

namespace PhotonConnectionMonitor
{
	class Program
	{
		const int StartDelay = 1000;
		const string HostUrl = "http://192.168.1.1";
		const string UserName = "admin";
		const string UserPassword = "admin";

		static void Main(string[] args)
		{
			ILogger logger = LogManager.GetCurrentClassLogger();
			logger.Info("Application start...");
			Task.Run(async () => {
				await Task.Delay(StartDelay);
				await new Worker(new WorkerConfig {
					HostUrl = HostUrl,
					UserName = UserName,
					UserPassword = UserPassword
				}).StartAsync();
			}).GetAwaiter().GetResult();
			logger.Info("Application has stopped.");
		}
	}
}

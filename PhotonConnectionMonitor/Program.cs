using System;
using System.Threading.Tasks;

namespace PhotonConnectionMonitor
{
	class Program
	{
		const int StartDelay = 1000;

		const string HostUrl = "http://192.169.1.1";

		static async void Main(string[] args)
		{
			await Task.Delay(StartDelay);
			await new Worker(new WorkerConfig { HostUrl = HostUrl }).StartAsync();
		}
	}
}

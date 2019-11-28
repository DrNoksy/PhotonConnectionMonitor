using RestSharp;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PhotonConnectionMonitor
{
	class Worker
	{
		private readonly string _homeUrl = "/home/home.html";
		private readonly string _csrfTokenMetaItemName = "csrf-token";
		private readonly RestClient _client;

		public WorkerConfig Config { get; }

		public Worker(WorkerConfig config) {
			this.Config = config;
			_client = new RestClient(Config.HostUrl);
		}

		private string GetMetaTagContent(string html, string metaItemName) {
			var metaTag = new Regex("<meta name=\"(.+?)\" content=\"(.+?)\">");
			var match = metaTag.Matches(html)
				.FirstOrDefault(m => m.Groups[1].Value == metaItemName && m.Groups[2].Value != null);
			return match == null
				? null
				: match.Groups[2].Value.Trim();
		}

		private async Task<string> GetCsrfTokenAsync() {
			var request = new RestRequest(_homeUrl, Method.GET);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			var content = response.Content;
			return GetMetaTagContent(content, _csrfTokenMetaItemName);
		}

		public async Task StartAsync() {
			string csrfToken = await GetCsrfTokenAsync();
			await Task.Delay(1000);
		}
	}
}

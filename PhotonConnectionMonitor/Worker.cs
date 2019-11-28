using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;
using static System.Text.Encoding;

namespace PhotonConnectionMonitor
{
	class Worker
	{
		private readonly string _homeUrl = "/html/home.html";
		private readonly string _loginUrl = "/api/user/login";
		private readonly string _stateLoginUrl = "/api/user/state-login";
		private readonly string _statusUrl = "/api/monitoring/status";
		private readonly string _csrfTokenMetaItemName = "csrf_token";
		private readonly string _requestTokenHeaderName = "__RequestVerificationToken";
		private readonly string _authTokenResponseHeaderName = "__RequestVerificationToken";
		private readonly string _setCookieHeaderName = "Set-Cookie";
		private readonly string _cookieHeaderName = "Cookie";
		private readonly int _defaultPasswordType = 4;
		private readonly RestClient _client;
		private string _authToken;
		private string _cookie;

		public WorkerConfig Config { get; }

		public Worker(WorkerConfig config) {
			Config = config;
			_client = new RestClient(Config.HostUrl);
		}

		private string GetMetaTagContent(string html, string metaItemName) {
			var metaTag = new Regex("<meta name=\"(.+?)\".*content=\"(.+?)\".*>");
			var match = metaTag.Matches(html)
				.FirstOrDefault(m => m.Groups[1].Value == metaItemName && m.Groups[2].Value != null);
			return match == null
				? null
				: match.Groups[2].Value.Trim();
		}

		private string GetHeader(IList<Parameter> headers, string headerName) {
			return headers.FirstOrDefault(header => header.Name == headerName)?.Value?.ToString();
		}

		private async Task<string> GetCsrfTokenAsync() {
			var request = new RestRequest(_homeUrl, Method.GET);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			var content = response.Content;
			return GetMetaTagContent(content, _csrfTokenMetaItemName);
		}

		private string ComputeUserPasswordHash(string userName, string userPassword, string csrfToken) {
			string firstLevelHash = System.Convert.ToBase64String(SHA256.Create().ComputeHash(ASCII.GetBytes(userPassword)));
			return System.Convert.ToBase64String(SHA256.Create().ComputeHash(ASCII.GetBytes(userName + firstLevelHash + csrfToken)));
		}

		private async Task Login() {
			string csrfToken = await GetCsrfTokenAsync();
			var request = new RestRequest(_loginUrl, Method.POST);
			string userName = Config.UserName;
			string userPasswordHash = ComputeUserPasswordHash(userName, Config.UserPassword, csrfToken);
			var data = new {
				Username = userName,
				Password = userPasswordHash,
				password_type = _defaultPasswordType
			};
			request.AddXmlBody(data);
			request.AddHeader(_requestTokenHeaderName, csrfToken);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			var content = response.Content;
			if (!content.Contains("<response>OK</response>")) {
				throw new UnauthorizedAccessException();
			}
			_authToken = GetHeader(response.Headers, _authTokenResponseHeaderName);
			_cookie = GetHeader(response.Headers, _setCookieHeaderName);
			if (_cookie != null) {
				_cookie = _cookie.Split(";")[0];
			}
		}

		public async Task StartAsync() {
			await Login();
			await Task.Delay(1000);
		}
	}
}

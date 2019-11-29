using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using static System.Text.Encoding;

namespace PhotonConnectionMonitor
{
	public enum ConnectionStatus
	{
		Empty = 0,
		Connected = 901,
		Disconnected = 902
	}

	public enum DialAction
	{
		Disconnect = 0,
		Connect = 1
	}

	[XmlRoot(ElementName = "request")]
	public class LoginRequest
	{
		public string Username;

		public string Password;

		[XmlElement(ElementName = "password_type")]
		public int PasswordType;
	}

	[XmlRoot(ElementName = "response")]
	public class ConnectionStatusResponse
	{
		public int ConnectionStatus;
	}

	[XmlRoot(ElementName = "request")]
	public class DialRequest
	{
		public int Action;
	}

	class Worker
	{
		private readonly string _homeUrl = "/html/home.html";
		private readonly string _loginUrl = "/api/user/login";
		private readonly string _stateLoginUrl = "/api/user/state-login";
		private readonly string _statusUrl = "/api/monitoring/status";
		private readonly string _dialUrl = "/api/dialup/dial";
		private readonly string _internetTestUrl = "https://www.google.com/";

		private readonly string _csrfTokenMetaItemName = "csrf_token";
		private readonly string _requestTokenHeaderName = "__RequestVerificationToken";
		private readonly string _setCookieHeaderName = "Set-Cookie";
		private readonly string _cookieHeaderName = "Cookie";

		private readonly int _defaultPasswordType = 4;
		private readonly int _reloginInterval = 3000;
		private readonly int _checkStatusInterval = 3000;
		private readonly int _lazyCheckStatusInterval = 10_000;
		private readonly int _initSessionRetryInterval = 10 * 60 * 1000;
		private readonly int _dialTimeout = 15_000;
		private readonly int _internetTestDelay = 3000;
		private readonly int _redialDelay = 3000;

		private readonly RestClient _client;
		private string _cookie;
		private Stack<string> _csrfTokens = new Stack<string>(2);

		public WorkerConfig Config { get; }

		public Worker(WorkerConfig config) {
			Config = config;
			_client = new RestClient(Config.HostUrl);
		}

		private async Task InitSessionAsync() {
			var request = new RestRequest(_homeUrl, Method.GET);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			_cookie = Utils.GetHttpHeader(response.Headers, _setCookieHeaderName);
			_csrfTokens = new Stack<string>(Utils.GetMetaTagContent(response.Content, _csrfTokenMetaItemName));
		}

		private void ClearSession() {
			this._cookie = null;
			this._csrfTokens.Clear();
		}

		private string ComputeUserPasswordHash(string userName, string userPassword, string csrfToken) {
			string hexFirstLevelHash = Utils.GetHexString(SHA256.Create().ComputeHash(UTF8.GetBytes(userPassword)));
			string base64FirstLevelHash = System.Convert.ToBase64String(UTF8.GetBytes(hexFirstLevelHash));
			string concatenatedString = userName + base64FirstLevelHash + csrfToken;
			string hexResultHash = Utils.GetHexString(SHA256.Create().ComputeHash(UTF8.GetBytes(concatenatedString)));
			string base64ResultHash = System.Convert.ToBase64String(UTF8.GetBytes(hexResultHash));
			return base64ResultHash;
		}

		private bool IsSessionEmpty()  => _cookie == null || _csrfTokens.Count == 0;

		private async Task TryInitSessionEndlesslyAsync() {
			if (IsSessionEmpty()) {
				await InitSessionAsync();
			}
			while (IsSessionEmpty()) {
				await Task.Delay(_initSessionRetryInterval);
				await InitSessionAsync();
			}
		}

		private async Task<bool> GetLoginStateAsync() {
			await TryInitSessionEndlesslyAsync();
			var request = new RestRequest(_stateLoginUrl, Method.GET);
			request.AddHeader(_cookieHeaderName, _cookie);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			var content = response.Content;
			return content == null || !content.Contains("<State>-1</State>");
		}

		private async Task<bool> LoginAsync() {
			if (await GetLoginStateAsync()) {
				return true;
			}
			await TryInitSessionEndlesslyAsync();
			var request = new RestRequest(_loginUrl, Method.POST);
			string csrfToken = _csrfTokens.Pop();
			string userName = Config.UserName;
			string userPasswordHash = ComputeUserPasswordHash(userName, Config.UserPassword, csrfToken);
			string xmlData = Utils.SerializeXml(new LoginRequest {
				Username = userName,
				Password = userPasswordHash,
				PasswordType = _defaultPasswordType
			});
			request.AddParameter("text/xml", xmlData, ParameterType.RequestBody);
			request.AddHeader(_cookieHeaderName, _cookie);
			request.AddHeader(_requestTokenHeaderName, csrfToken);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			_csrfTokens.Push(Utils.GetHttpHeader(response.Headers, _requestTokenHeaderName));
			_cookie = Utils.GetHttpHeader(response.Headers, _setCookieHeaderName);
			if (_cookie != null) {
				_cookie = _cookie.Split(";")[0];
			}
			if (!response.Content.Contains("<response>OK</response>")) {
				return false;
			}
			return await GetLoginStateAsync();
		}

		private async Task<ConnectionStatus> GetConnectionStatusAsync() {
			await TryInitSessionEndlesslyAsync();
			var request = new RestRequest(_statusUrl, Method.GET);
			request.AddHeader(_cookieHeaderName, _cookie);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			try {
				ConnectionStatusResponse responseObject = Utils.DeserializeXml<ConnectionStatusResponse>(response.Content);
				return (ConnectionStatus)responseObject.ConnectionStatus;
			} catch {
				return ConnectionStatus.Empty;
			}
		}

		private async Task<bool> GetIsConnectedAsync() {
			return await GetConnectionStatusAsync() == ConnectionStatus.Connected;
		}

		private async Task<bool> TestInternet() {
			var client = new RestClient(_internetTestUrl);
			var request = new RestRequest("", Method.GET);
			IRestResponse response = await client.ExecuteTaskAsync(request);
			return response.StatusCode == System.Net.HttpStatusCode.OK;
		}

		private async Task<bool> DialAsync(DialAction action, bool isRedial = false) {
			await TryInitSessionEndlesslyAsync();
			var request = new RestRequest(_dialUrl, Method.POST);
			request.AddHeader(_cookieHeaderName, _cookie);
			request.AddHeader(_requestTokenHeaderName, _csrfTokens.Pop());
			string xmlData = Utils.SerializeXml(new DialRequest {
				Action = (int)action
			});
			request.AddParameter("text/xml", xmlData, ParameterType.RequestBody);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			_csrfTokens.Push(Utils.GetHttpHeader(response.Headers, _requestTokenHeaderName));
			if (!response.Content.Contains("<response>OK</response>")) {
				return false;
			}
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			while (!await GetIsConnectedAsync() && stopwatch.ElapsedMilliseconds <= _dialTimeout) {
				await Task.Delay(_checkStatusInterval);
			}
			bool failedByTimeout = stopwatch.ElapsedMilliseconds > _dialTimeout;
			if (action == DialAction.Connect && !failedByTimeout && !isRedial) {
				await Task.Delay(_internetTestDelay);
				if (!await TestInternet()) {
					await DialAsync(DialAction.Disconnect);
					await Task.Delay(_redialDelay);
					return await DialAsync(DialAction.Connect, true);
				}
			}
			return !failedByTimeout;
		}

		private async Task TryReloginEndlesslyAsync() {
			while (!await LoginAsync()) {
				await Task.Delay(_reloginInterval);
			}
		}

		private async Task TryReconnectEndlesslyAsync() {
			while (!await GetIsConnectedAsync()) {
				await TryReloginEndlesslyAsync();
				if (!await DialAsync(DialAction.Connect)) {
					ClearSession();
				}
			}
		}

		public async Task StartAsync() {
			while (true) {
				try {
					await TryReconnectEndlesslyAsync();
				} catch {
				} finally {
					await Task.Delay(_lazyCheckStatusInterval);
				}
			}
		}
	}
}

using NLog;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
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

		private readonly string _csrfTokenMetaItemName = "csrf_token";
		private readonly string _requestTokenHeaderName = "__RequestVerificationToken";
		private readonly string _setCookieHeaderName = "Set-Cookie";
		private readonly string _cookieHeaderName = "Cookie";

		private readonly int _defaultPasswordType = 4;

		private readonly int _redialDelay = 3000;

		private readonly int _reloginInterval = 3000;
		private readonly int _checkStatusInterval = 3000;
		private readonly int _lazyCheckStatusInterval = 10_000;
		private readonly int _initSessionRetryInterval = 3000;
		private readonly int _internetTestRetryInterval = 100;
		private readonly int _reconnectAfterTotalFailInterval = 15 * 60 * 1000; // 15 minutes

		private readonly int _dialTimeout = 20_000;
		private readonly int _internetTestTimeout = 1000;

		private readonly int _maxInternetConnectRetries = 5;
		private readonly int _maxInternetTestRetries = 3;
		private readonly int _maxReconnectRetries = 5;
		private readonly int _maxInitSessionRetries = 20;

		private readonly RestClient _client;
		private readonly ILogger _logger = LogManager.GetCurrentClassLogger();
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
			_logger.Debug($"Session was initialized with auth cookie '{_cookie}'");
		}

		private void ClearSession() {
			this._cookie = null;
			this._csrfTokens.Clear();
			_logger.Debug($"Session was reset");
		}

		private string ComputeUserPasswordHash(string userName, string userPassword, string csrfToken) {
			string hexFirstLevelHash = Utils.GetHexString(SHA256.Create().ComputeHash(UTF8.GetBytes(userPassword)));
			string base64FirstLevelHash = System.Convert.ToBase64String(UTF8.GetBytes(hexFirstLevelHash));
			string concatenatedString = userName + base64FirstLevelHash + csrfToken;
			string hexResultHash = Utils.GetHexString(SHA256.Create().ComputeHash(UTF8.GetBytes(concatenatedString)));
			string base64ResultHash = System.Convert.ToBase64String(UTF8.GetBytes(hexResultHash));
			return base64ResultHash;
		}

		private bool IsSessionEmpty() => _cookie == null || _csrfTokens.Count == 0;

		private async Task<bool> TryInitSessionAsync() {
			int retries = 0;
			while (IsSessionEmpty() && retries++ < _maxInitSessionRetries) {
				if (retries > 1) {
					await Task.Delay(_initSessionRetryInterval);
				}
				await InitSessionAsync();
			}
			return !IsSessionEmpty();
		}

		private async Task<bool> GetLoginStateAsync() {
			bool isLoggedIn;
			if (IsSessionEmpty()) {
				isLoggedIn = false;
			} else {
				var request = new RestRequest(_stateLoginUrl, Method.GET);
				request.AddHeader(_cookieHeaderName, _cookie);
				IRestResponse response = await _client.ExecuteTaskAsync(request);
				var content = response.Content;
				isLoggedIn = content == null || !content.Contains("<State>-1</State>");
			}
			string message = isLoggedIn ? "logged in" : "not logged in";
			_logger.Debug($"Login state: {message}");
			return isLoggedIn;
		}

		private async Task<bool> LoginAsync() {
			bool isLoggedIn;
			if (!await TryInitSessionAsync()) {
				isLoggedIn = false;
			} else {
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
				isLoggedIn = response.Content.Contains("<response>OK</response>");
			}
			string message = isLoggedIn ? "success" : "fail";
			_logger.Info($"Login: {message}");
			return isLoggedIn && await GetLoginStateAsync();
		}

		private async Task<ConnectionStatus> GetConnectionStatusAsync() {
			_logger.Debug("Checking connection status...");
			ConnectionStatus result;
			if (IsSessionEmpty()) {
				result = ConnectionStatus.Empty;
			} else {
				var request = new RestRequest(_statusUrl, Method.GET);
				request.AddHeader(_cookieHeaderName, _cookie);
				IRestResponse response = await _client.ExecuteTaskAsync(request);
				try {
					ConnectionStatusResponse responseObject = Utils.DeserializeXml<ConnectionStatusResponse>(response.Content);
					result = (ConnectionStatus)responseObject.ConnectionStatus;
				} catch {
					result = ConnectionStatus.Empty;
				}
			}
			_logger.Debug($"Connection status: {result.ToString()}");
			return result;
		}

		private async Task<bool> TestInternetAsync() {
			bool result;
			try {
				string host = "8.8.8.8";
				byte[] buffer = new byte[32];
				PingReply reply = await new Ping().SendPingAsync(host, _internetTestTimeout, buffer, new PingOptions());
				result = reply.Status == IPStatus.Success;
			} catch (Exception) {
				result = false;
			}
			string message = result ? "success" : "fail";
			_logger.Debug($"Internet test: {message}");
			return result;
		}

		private async Task<bool> GetIsConnectedAsync() {
			return await GetConnectionStatusAsync() == ConnectionStatus.Connected && await TryTestInternetAsync();
		}

		private async Task<bool> GetIsDialSuccesAsync(DialAction action) {
			ConnectionStatus connectionStatus = await GetConnectionStatusAsync();
			return action == DialAction.Connect
				? connectionStatus == ConnectionStatus.Connected
				: connectionStatus != ConnectionStatus.Connected;
		}

		private async Task<bool> DialAsync(DialAction action) {
			if (!await TryInitSessionAsync()) {
				return false;
			}
			var request = new RestRequest(_dialUrl, Method.POST);
			request.AddHeader(_cookieHeaderName, _cookie);
			request.AddHeader(_requestTokenHeaderName, _csrfTokens.Pop());
			string xmlData = Utils.SerializeXml(new DialRequest {
				Action = (int)action
			});
			request.AddParameter("text/xml", xmlData, ParameterType.RequestBody);
			IRestResponse response = await _client.ExecuteTaskAsync(request);
			_csrfTokens.Push(Utils.GetHttpHeader(response.Headers, _requestTokenHeaderName));
			bool success = response.Content.Contains("<response>OK</response>");
			string message = success ? "success" : "fail";
			_logger.Debug($"Dial ({action.ToString()}): {message}");
			if (!success) {
				return false;
			}
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			while (!await GetIsDialSuccesAsync(action) && stopwatch.ElapsedMilliseconds <= _dialTimeout) {
				await Task.Delay(_checkStatusInterval);
			}
			bool failedByTimeout = stopwatch.ElapsedMilliseconds > _dialTimeout;
			message = !failedByTimeout ? "success" : "fail";
			_logger.Debug($"Dial result ({action.ToString()}): {message}");
			return !failedByTimeout;
		}

		private async Task<bool> ConnectAsync() {
			_logger.Debug("Trying to connect...");
			Func<Task<bool>> connect = async () => await DialAsync(DialAction.Connect) && await TryTestInternetAsync();
			bool connectedSuccess = await connect();
			int retryNumber = 0;
			while (!connectedSuccess && retryNumber++ <= _maxInternetConnectRetries) {
				await DisonnectAsync();
				await Task.Delay(_redialDelay);
				connectedSuccess = await connect();
			}
			string message = connectedSuccess ? "was established" : "failed";
			_logger.Info($"Connection {message}");
			return connectedSuccess;
		}

		private async Task<bool> DisonnectAsync() {
			_logger.Debug("Trying to disconnect...");
			Func<Task<bool>> disconnect = async () => await DialAsync(DialAction.Disconnect);
			bool disconnectedSuccess = await disconnect();
			disconnectedSuccess = disconnectedSuccess || await disconnect();
			string message = disconnectedSuccess ? "success" : "fail";
			_logger.Debug($"Disconnect: {message}");
			return disconnectedSuccess;
		}

		private async Task<bool> TryReloginAsync() {
			bool success;
			int retries = 0;
			while (!(success = await LoginAsync()) && retries++ < _maxReconnectRetries) {
				await Task.Delay(_reloginInterval);
			}
			return success;
		}

		private async Task<bool> TryTestInternetAsync() {
			bool success;
			int retries = 0;
			while (!(success = await TestInternetAsync()) && retries++ < _maxInternetTestRetries) {
				await Task.Delay(_internetTestRetryInterval);
			}
			return success;
		}

		private async Task<bool> TryReconnectAsync() {
			int retries = 0;
			bool isConnected;
			while (!(isConnected = await GetIsConnectedAsync()) && retries++ < _maxReconnectRetries) {
				bool isLoggedIn = await GetLoginStateAsync() || await TryReloginAsync();
				if (!isLoggedIn || !await ConnectAsync()) {
					ClearSession();
				}
			}
			return isConnected;
		}

		public async Task StartAsync() {
			while (true) {
				bool connected = false;
				try {
					connected = await TryInitSessionAsync() && await TryReconnectAsync();
				} catch (Exception ex) {
					_logger.Error(ex);
				} finally {
					_logger.Info(connected
						? $"Internet is connected, checking status after {_lazyCheckStatusInterval} ms"
						: $"Internet connection can not be established, retry after {_reconnectAfterTotalFailInterval}");
					await Task.Delay(connected ? _lazyCheckStatusInterval : _reconnectAfterTotalFailInterval);
				}
			}
		}
	}
}

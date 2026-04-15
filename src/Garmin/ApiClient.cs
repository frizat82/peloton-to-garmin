using Common.Dto;
using Common.Http;
using Common.Observe;
using Common.Service;
using Flurl.Http;
using Garmin.Auth;
using Garmin.Dto;
using OAuth;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Garmin
{
	public interface IGarminApiClient
	{
		Task<CookieJar> InitCookieJarAsync(object queryParams);
		Task<GarminResult> GetCsrfTokenAsync(object queryParams, CookieJar jar);
		Task<SendCredentialsResult> SendCredentialsAsync(string email, string password, object queryParams, object loginData, CookieJar jar);
		Task<string> SendMfaCodeAsync(object queryParams, object mfaData, CookieJar jar);
		Task<string> GetOAuth1TokenAsync(ConsumerCredentials credentials, string ticket);
		Task<OAuth2Token> GetOAuth2TokenAsync(OAuth1Token oAuth1Token, ConsumerCredentials credentials);
		Task<ConsumerCredentials> GetConsumerCredentialsAsync();
		Task<UploadResponse> UploadActivity(string filePath, string format, GarminApiAuthentication auth);
		Task<ICollection<GarminActivitySummary>> SearchActivitiesAsync(DateTime startDate, DateTime endDate, GarminApiAuthentication auth);
		Task UpdateActivityAsync(long activityId, GarminActivityUpdateRequest request, GarminApiAuthentication auth);
		Task<byte[]> DownloadActivityFitAsync(long activityId, GarminApiAuthentication auth);
		Task DeleteActivityAsync(long activityId, GarminApiAuthentication auth);
		Task<long?> PollUploadActivityIdAsync(long uploadId, GarminApiAuthentication auth);
	}

	public class ApiClient : IGarminApiClient
	{
		private ISettingsService _settingsService;

		private static readonly ILogger _logger = LogContext.ForClass<ApiClient>();

		public ApiClient(ISettingsService settingsService)
		{
			_settingsService = settingsService;
		}

		public Task<ConsumerCredentials> GetConsumerCredentialsAsync()
		{
			return "https://thegarth.s3.amazonaws.com/oauth_consumer.json"
				.GetJsonAsync<ConsumerCredentials>();
		}

		public async Task<CookieJar> InitCookieJarAsync(object queryParams)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			await setttings.Garmin.Api.SsoEmbedUrl
						.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
						.WithHeader("origin", setttings.Garmin.Api.Origin)
						.SetQueryParams(queryParams)
						.WithCookies(out var jar)
						.GetStringAsync();

			return jar;
		}

		public async Task<SendCredentialsResult> SendCredentialsAsync(string email, string password, object queryParams, object loginData, CookieJar jar)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			var result = new SendCredentialsResult();
			result.RawResponseBody = await setttings.Garmin.Api.SsoSignInUrl
						.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
						.WithHeader("origin", setttings.Garmin.Api.Origin)
						.WithHeader("referer", setttings.Garmin.Api.Referer)
						.WithHeader("NK", "NT")
						.SetQueryParams(queryParams)
						.WithCookies(jar)
						.OnRedirect((r) => { result.WasRedirected = true; result.RedirectedTo = r.Redirect.Url; })
						.PostUrlEncodedAsync(loginData)
						.ReceiveString();

			return result;
		}

		public async Task<GarminResult> GetCsrfTokenAsync(object queryParams, CookieJar jar)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			var result = new GarminResult();
			result.RawResponseBody = await setttings.Garmin.Api.SsoSignInUrl
						.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
						.WithHeader("origin", setttings.Garmin.Api.Origin)
						.SetQueryParams(queryParams)
						.WithCookies(jar)
						.GetAsync()
						.ReceiveString();

			return result;
		}

		public async Task<string> SendMfaCodeAsync(object queryParams, object mfaData, CookieJar jar)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			return await setttings.Garmin.Api.SsoMfaCodeUrl
						.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
						.WithHeader("origin", setttings.Garmin.Api.Origin)
						.SetQueryParams(queryParams)
						.WithCookies(jar)
						.OnRedirect(redir => redir.Request.WithCookies(jar))
						.PostUrlEncodedAsync(mfaData)
						.ReceiveString();
		}

		public async Task<string> GetOAuth1TokenAsync(ConsumerCredentials credentials, string ticket)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			OAuthRequest oauthClient = OAuthRequest.ForRequestToken(credentials.Consumer_Key, credentials.Consumer_Secret);
			oauthClient.RequestUrl = $"{setttings.Garmin.Api.OAuth1TokenUrl}?ticket={ticket}&login-url={setttings.Garmin.Api.OAuth1LoginUrlParam}";

			return await oauthClient.RequestUrl
							.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
							.WithHeader("Authorization", oauthClient.GetAuthorizationHeader())
							.GetStringAsync();
		}
		public async Task<OAuth2Token> GetOAuth2TokenAsync(OAuth1Token oAuth1Token, ConsumerCredentials credentials)
		{
			var setttings = await _settingsService.GetSettingsAsync();

			OAuthRequest oauthClient2 = OAuthRequest.ForProtectedResource("POST", credentials.Consumer_Key, credentials.Consumer_Secret, oAuth1Token.Token, oAuth1Token.TokenSecret);
			oauthClient2.RequestUrl = setttings.Garmin.Api.OAuth2RequestUrl;

			return await oauthClient2.RequestUrl
								.WithHeader("User-Agent", setttings.Garmin.Api.SsoUserAgent)
								.WithHeader("Authorization", oauthClient2.GetAuthorizationHeader())
								.WithHeader("Content-Type", "application/x-www-form-urlencoded") // this header is required, without it you get a 500
								.PostUrlEncodedAsync(new object()) // hack: PostAsync() will drop the content-type header, by posting empty object we trick flurl into leaving the header
								.ReceiveJson<OAuth2Token>();
		}

		private static IFlurlRequest WithConnectApiHeaders(string url, GarminApiAuthentication auth, GarminApiSettings api)
		{
			return url
				.WithOAuthBearerToken(auth.OAuth2Token.Access_Token)
				.WithHeader("NK", api.UplaodActivityNkHeader)
				.WithHeader("origin", api.Origin)
				.WithHeader("User-Agent", api.UploadActivityUserAgent);
		}

		public async Task<UploadResponse> UploadActivity(string filePath, string format, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();

			var fileName = Path.GetFileName(filePath);
			var rawJson = await WithConnectApiHeaders($"{settings.Garmin.Api.UploadActivityUrl}/{format}", auth, settings.Garmin.Api)
				.AllowHttpStatus("2xx,409")
				.PostMultipartAsync((data) =>
				{
					data.AddFile("\"file\"", path: filePath, contentType: "application/octet-stream", fileName: $"\"{fileName}\"");
				})
				.ReceiveString();
			_logger.Debug("Upload response: {Json}", rawJson);
			var response = System.Text.Json.JsonSerializer.Deserialize<UploadResponse>(rawJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			var result = response.DetailedImportResult;

			if (result.Failures.Any())
			{
				foreach (var failure in result.Failures)
				{
					if (failure.Messages.Any())
					{
						foreach (var message in failure.Messages)
						{
							if (message.Code == 202)
							{
								_logger.Information("Activity already uploaded {garminWorkout}", result.FileName);
							}
							else
							{
								_logger.Error("Failed to upload activity to Garmin. Message: {errorMessage}", message);
							}
						}
					}
				}
			}

			return response;
		}

		public async Task<ICollection<GarminActivitySummary>> SearchActivitiesAsync(DateTime startDate, DateTime endDate, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();

			return await WithConnectApiHeaders(settings.Garmin.Api.ActivitySearchUrl, auth, settings.Garmin.Api)
				.SetQueryParams(new
				{
					startDate = startDate.ToString("yyyy-MM-dd"),
					endDate = endDate.ToString("yyyy-MM-dd"),
					start = 0,
					limit = 50
				})
				.GetJsonAsync<ICollection<GarminActivitySummary>>();
		}

		public async Task UpdateActivityAsync(long activityId, GarminActivityUpdateRequest request, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();

			var requestJson = System.Text.Json.JsonSerializer.Serialize(request);
			_logger.Information("UpdateActivity PUT body: {Body}", requestJson);

			var response = await WithConnectApiHeaders($"{settings.Garmin.Api.ActivityUpdateUrl}/{activityId}", auth, settings.Garmin.Api)
				.PutJsonAsync(request);

			var responseBody = await response.GetStringAsync();
			_logger.Information("UpdateActivity response {Status}: {Body}", response.StatusCode, responseBody);
		}

		public async Task<byte[]> DownloadActivityFitAsync(long activityId, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();

			var bytes = await WithConnectApiHeaders($"{settings.Garmin.Api.ActivityDownloadUrl}/{activityId}", auth, settings.Garmin.Api)
				.GetBytesAsync();

			// Garmin returns a ZIP archive containing the FIT file — extract it
			if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
			{
				using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
				var fitEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".fit", StringComparison.OrdinalIgnoreCase));
				if (fitEntry is not null)
				{
					using var ms = new MemoryStream();
					using var stream = fitEntry.Open();
					await stream.CopyToAsync(ms);
					_logger.Information("Extracted {FileName} ({Bytes} bytes) from Garmin ZIP download", fitEntry.Name, ms.Length);
					return ms.ToArray();
				}
				_logger.Warning("Downloaded ZIP for activity {ActivityId} but found no .fit entry inside", activityId);
			}

			return bytes;
		}

		public async Task DeleteActivityAsync(long activityId, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();

			_logger.Information("Deleting Garmin activity {ActivityId} to replace with merged FIT", activityId);

			await WithConnectApiHeaders($"{settings.Garmin.Api.ActivityUpdateUrl}/{activityId}", auth, settings.Garmin.Api)
				.DeleteAsync();
		}

		public async Task<long?> PollUploadActivityIdAsync(long uploadId, GarminApiAuthentication auth)
		{
			var settings = await _settingsService.GetSettingsAsync();
			var url = $"{settings.Garmin.Api.UploadActivityUrl}/{uploadId}";

			for (int attempt = 1; attempt <= 8; attempt++)
			{
				await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 3));

				try
				{
					var response = await WithConnectApiHeaders(url, auth, settings.Garmin.Api)
						.AllowAnyHttpStatus()
						.GetAsync();

					var rawJson = await response.GetStringAsync();
					_logger.Information("FIT merge: poll attempt {Attempt} status {Status}: {Json}", attempt, response.StatusCode, rawJson);

					if (!response.ResponseMessage.IsSuccessStatusCode)
					{
						_logger.Warning("FIT merge: poll returned {Status}, stopping", response.StatusCode);
						return null;
					}

					var uploadResponse = System.Text.Json.JsonSerializer.Deserialize<UploadResponse>(rawJson,
						new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

					var internalId = uploadResponse?.DetailedImportResult?.Successes?.FirstOrDefault()?.InternalId;
					if (internalId is not null)
					{
						_logger.Information("FIT merge: new Garmin activity ID {NewId} resolved after {Attempt} poll(s)", internalId, attempt);
						return internalId;
					}

					_logger.Information("FIT merge: upload {UploadId} still processing (attempt {Attempt}/8)", uploadId, attempt);
				}
				catch (Exception e)
				{
					_logger.Warning(e, "FIT merge: poll attempt {Attempt} threw {Message}", attempt, e.Message);
					return null;
				}
			}

			_logger.Warning("FIT merge: could not resolve new activity ID for upload {UploadId} after polling", uploadId);
			return null;
		}
	}
}

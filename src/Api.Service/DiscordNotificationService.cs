using Common.Observe;
using Serilog;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Api.Service;

public interface IDiscordNotificationService
{
	Task SendSuccessAsync(int workoutCount);
	Task SendFailureAsync(string errorMessage);
}

public class DiscordNotificationService : IDiscordNotificationService
{
	private static readonly ILogger _logger = LogContext.ForClass<DiscordNotificationService>();
	private readonly string _webhookUrl;
	private readonly bool _notifyOnSuccess;
	private readonly HttpClient _httpClient;

	public DiscordNotificationService(string webhookUrl, bool notifyOnSuccess, HttpClient httpClient)
	{
		_webhookUrl = webhookUrl;
		_notifyOnSuccess = notifyOnSuccess;
		_httpClient = httpClient;
	}

	public async Task SendSuccessAsync(int workoutCount)
	{
		if (!_notifyOnSuccess || string.IsNullOrWhiteSpace(_webhookUrl))
			return;

		var content = workoutCount > 0
			? $"✅ P2G sync complete — {workoutCount} workout(s) synced to Garmin."
			: "✅ P2G sync complete — no new workouts to sync.";

		await PostAsync(content);
	}

	public async Task SendFailureAsync(string errorMessage)
	{
		if (string.IsNullOrWhiteSpace(_webhookUrl))
			return;

		await PostAsync($"❌ P2G sync failed: {errorMessage}");
	}

	private async Task PostAsync(string message)
	{
		try
		{
			var payload = new { content = message };
			var response = await _httpClient.PostAsJsonAsync(_webhookUrl, payload);
			if (!response.IsSuccessStatusCode)
				_logger.Warning("Discord webhook returned {StatusCode}", response.StatusCode);
		}
		catch (System.Exception e)
		{
			_logger.Warning(e, "Failed to send Discord notification: {Message}", e.Message);
		}
	}
}

public class NullDiscordNotificationService : IDiscordNotificationService
{
	public Task SendSuccessAsync(int workoutCount) => Task.CompletedTask;
	public Task SendFailureAsync(string errorMessage) => Task.CompletedTask;
}

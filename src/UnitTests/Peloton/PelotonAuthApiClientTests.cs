using Common.Dto;
using Common.Service;
using FluentAssertions;
using Flurl.Http.Testing;
using Moq;
using NUnit.Framework;
using Peloton.Auth;
using Peloton.Dto;
using System.Threading.Tasks;

namespace UnitTests.PelotonTests
{
	public class PelotonAuthApiClientTests
	{
		/// <summary>Signs in against a fake Peloton whose credentials step answers with <paramref name="status"/>.</summary>
		private static Task<global::Common.Stateful.PelotonApiAuthentication> SignInWhenLoginAnswers(HttpTest http, string body, int status)
		{
			var settings = new Settings();
			settings.Peloton.Email = "rider@example.com";
			settings.Peloton.Password = "not-a-real-password";
			var settingsService = new Mock<ISettingsService>();
			settingsService.Setup(s => s.GetSettingsAsync()).ReturnsAsync(settings);

			http.RespondWith("", 200, new { Set_Cookie = "_csrf=token; Path=/usernamepassword/login" });
			http.RespondWith(body, status);
			return new PelotonAuthApiClient(settingsService.Object).Authenticate();
		}

		[Test]
		public async Task Authenticate_WhenPelotonBlocksTooManyLogins_ReportsTheBlock()
		{
			using var http = new HttpTest();

			var act = () => SignInWhenLoginAnswers(http, "{\"statusCode\":429,\"name\":\"AnomalyDetected\",\"code\":\"too_many_attempts\"}", 429);

			(await act.Should().ThrowAsync<PelotonAuthenticationError>())
				.WithMessage("*too many recent login attempts (HTTP 429)*too_many_attempts*");
		}

		[Test]
		public async Task Authenticate_WhenPelotonRejectsTheLogin_ReportsTheStatus()
		{
			using var http = new HttpTest();

			var act = () => SignInWhenLoginAnswers(http, "{\"code\":\"access_denied\"}", 403);

			(await act.Should().ThrowAsync<PelotonAuthenticationError>())
				.WithMessage("*HTTP 403*access_denied*");
		}
	}
}

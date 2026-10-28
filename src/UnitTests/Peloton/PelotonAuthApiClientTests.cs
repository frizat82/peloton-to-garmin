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
		[Test]
		public async Task Authenticate_WhenPelotonBlocksTooManyLogins_ReportsTheBlock()
		{
			var settings = new Settings();
			settings.Peloton.Email = "rider@example.com";
			settings.Peloton.Password = "not-a-real-password";
			var settingsService = new Mock<ISettingsService>();
			settingsService.Setup(s => s.GetSettingsAsync()).ReturnsAsync(settings);

			using var http = new HttpTest();
			http.RespondWith("", 200, new { Set_Cookie = "_csrf=token; Path=/usernamepassword/login" });
			http.RespondWith("{\"statusCode\":429,\"name\":\"AnomalyDetected\",\"code\":\"too_many_attempts\"}", 429);

			var act = () => new PelotonAuthApiClient(settingsService.Object).Authenticate();

			(await act.Should().ThrowAsync<PelotonAuthenticationError>())
				.WithMessage("*too many recent login attempts (HTTP 429)*too_many_attempts*");
		}
	}
}

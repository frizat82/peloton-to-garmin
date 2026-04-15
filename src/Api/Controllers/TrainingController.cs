using Api.Contract;
using Microsoft.AspNetCore.Mvc;
using Peloton;
using Peloton.Dto;
using Sync;

namespace Api.Controllers;

[ApiController]
[Produces("application/json")]
[Consumes("application/json")]
public class TrainingController : Controller
{
	private readonly ITrainingAnalysisService _trainingAnalysis;

	public TrainingController(ITrainingAnalysisService trainingAnalysis)
	{
		_trainingAnalysis = trainingAnalysis;
	}

	/// <summary>
	/// Returns your current training state (CTL/ATL/TSB) and a recommended workout for today
	/// based on your recent Peloton workout history.
	/// </summary>
	/// <response code="200">Training state and recommendation.</response>
	/// <response code="500">Unhandled exception.</response>
	[HttpGet]
	[Route("api/training")]
	[ProducesResponseType(StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
	public async Task<ActionResult<TrainingStateGetResponse>> GetAsync()
	{
		try
		{
			var state = await _trainingAnalysis.GetTrainingStateAsync();
			return Ok(state);
		}
		catch (PelotonAuthenticationError pe)
		{
			return StatusCode(StatusCodes.Status500InternalServerError,
				new ErrorResponse($"Peloton authentication error: {pe.Message}"));
		}
		catch (Exception e)
		{
			return StatusCode(StatusCodes.Status500InternalServerError,
				new ErrorResponse($"Unexpected error: {e.Message}"));
		}
	}
}

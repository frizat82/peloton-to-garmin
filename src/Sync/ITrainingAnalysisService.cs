using Api.Contract;
using System.Threading.Tasks;

namespace Sync;

public interface ITrainingAnalysisService
{
	Task<TrainingStateGetResponse> GetTrainingStateAsync();
}

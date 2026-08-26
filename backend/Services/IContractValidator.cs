using Kairon.Backend.DTOs;

namespace Kairon.Backend.Services;

public interface IContractValidator
{
    List<MismatchDto> Compare(Dictionary<string, string> expected, Dictionary<string, object> actual, string path = "");
}

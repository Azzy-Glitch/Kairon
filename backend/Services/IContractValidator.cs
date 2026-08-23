using AIDIP.Backend.DTOs;

namespace AIDIP.Backend.Services;

public interface IContractValidator
{
    List<MismatchDto> Compare(Dictionary<string, string> expected, Dictionary<string, object> actual, string path = "");
}

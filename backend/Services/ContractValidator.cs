using AIDIP.Backend.DTOs;
using System.Text.Json;

namespace AIDIP.Backend.Services;

public class ContractValidator : IContractValidator
{
    public List<MismatchDto> Compare(Dictionary<string, string> expected, Dictionary<string, object> actual, string path = "")
    {
        var issues = new List<MismatchDto>();

        foreach (var kv in expected)
        {
            var fieldPath = string.IsNullOrEmpty(path) ? kv.Key : $"{path}.{kv.Key}";

            if (!actual.ContainsKey(kv.Key))
            {
                issues.Add(new MismatchDto { Path = fieldPath, Issue = "missing field" });
                continue;
            }

            if (!TypeMatches(actual[kv.Key], kv.Value))
            {
                issues.Add(new MismatchDto
                {
                    Path = fieldPath,
                    Issue = "type mismatch",
                    Expected = kv.Value,
                    Actual = JsTypeName(actual[kv.Key])
                });
            }
        }

        return issues;
    }

    private static bool TypeMatches(object value, string expectedType) =>
        JsTypeName(value).Equals(expectedType, StringComparison.OrdinalIgnoreCase);

    private static string JsTypeName(object value) => value switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => je.TryGetInt64(out _) ? "integer" : "double",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "null"
        },
        _ => value?.GetType().Name.ToLowerInvariant() ?? "null"
    };
}

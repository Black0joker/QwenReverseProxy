using System.Text;
using System.Text.Json;
using QwenProxy.Sse;

Console.OutputEncoding = Encoding.UTF8;

// Read test inputs (JSON array of strings) and print one JSON object per line:
// {"input": ..., "output": ... | null, "error": ... | null}
string inputPath = args.Length > 0 ? args[0] : "cases.json";
string casesJson = File.ReadAllText(inputPath);
var cases = JsonSerializer.Deserialize<List<string>>(casesJson) ?? new();

foreach (var input in cases)
{
    string? output = null;
    string? error = null;
    try
    {
        output = JsonRepair.Repair(input);
        // Validate the repaired output parses as JSON (like repair_json_text)
        try
        {
            using var doc = JsonDocument.Parse(output);
        }
        catch (Exception ve)
        {
            error = $"invalid-after-repair: {ve.Message}";
        }
    }
    catch (Exception e)
    {
        error = e.Message;
    }

    var line = new Dictionary<string, string?>
    {
        ["input"] = input,
        ["output"] = output,
        ["error"] = error,
    };
    Console.WriteLine(JsonSerializer.Serialize(line));
}

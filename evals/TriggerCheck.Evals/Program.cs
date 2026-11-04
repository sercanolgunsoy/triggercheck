using System.Globalization;
using TriggerCheck.Evals;

// AI-11 eval runner. See evals/README.md.
//   dotnet run --project evals/TriggerCheck.Evals                       fake client (canned outputs), free
//   dotnet run --project evals/TriggerCheck.Evals -- --real --yes       real Claude API (ANTHROPIC_API_KEY), paid
// Options: --feature <Name>, --case <text>, --model <id>, --effort <level>, --threshold 0.8, --cases <dir>, --out <dir>
// Exit code: 0 gate passed, 1 gate failed, 2 usage error.

var argsList = args.ToList();
string? Arg(string name)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null;
}
bool Flag(string name) => argsList.Contains(name);

var casesDir = Arg("--cases") ?? EvalScenario.FindCasesDir(Directory.GetCurrentDirectory()) ?? EvalScenario.FindCasesDir(AppContext.BaseDirectory);
if (casesDir is null || !Directory.Exists(casesDir))
{
    Console.Error.WriteLine("evals/cases not found; pass --cases <dir>.");
    return 2;
}
var outDir = Arg("--out") ?? Path.Combine(Path.GetDirectoryName(casesDir)!, "reports");
var real = Flag("--real");
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (real && string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("--real needs ANTHROPIC_API_KEY.");
    return 2;
}

var scenarios = EvalScenario.LoadAll(casesDir);
var options = new EvalOptions
{
    Real = real, ApiKey = apiKey, Model = Arg("--model"), Effort = Arg("--effort"), Feature = Arg("--feature"), Filter = Arg("--case"),
    Threshold = Arg("--threshold") is { } t ? double.Parse(t, CultureInfo.InvariantCulture) : EvalGrader.DefaultThreshold,
};

if (real && !Flag("--yes"))
{
    // Paid run: show the size first (README: estimate and get approval before running).
    var count = scenarios.Count(s => (options.Feature is null || s.Feature == options.Feature) && (options.Filter is null || s.Id.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)));
    Console.WriteLine($"{count} case(s) would be sent to the Claude API (about 3-5k input and 0.5-2k output tokens each). Re-run with --yes to proceed.");
    return 2;
}

var report = await EvalHarness.RunAsync(scenarios, options);
report.Write(outDir);
Console.WriteLine(report.ToMarkdown());
Console.WriteLine($"Report written to {Path.GetFullPath(outDir)}");
return report.GatePassed ? 0 : 1;

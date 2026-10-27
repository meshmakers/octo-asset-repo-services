using System.Text;

namespace AssetRepositoryServices.UnitTests.Adversarial;

/// <summary>
///     Records every case of the asset-repo adversarial suite (AB#6915): case, attack, test, observed result, side
///     effect. The rows go to <c>adversarial-results.md</c> next to the test assembly, so a re-run yields the table.
/// </summary>
internal static class AdversarialCaseLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "adversarial-results.md");
    private static bool _started;

    public static void Record(string caseId, string attack, string test, string observed, string sideEffect)
    {
        lock (Gate)
        {
            var text = new StringBuilder();
            if (!_started)
            {
                _started = true;
                text.AppendLine("| Case | Attack | Test | Observed | Side effect |");
                text.AppendLine("|------|--------|------|----------|-------------|");
                File.WriteAllText(FilePath, string.Empty);
            }

            text.AppendLine($"| {caseId} | {attack} | `{test}` | {observed} | {sideEffect} |");
            File.AppendAllText(FilePath, text.ToString());
        }
    }
}

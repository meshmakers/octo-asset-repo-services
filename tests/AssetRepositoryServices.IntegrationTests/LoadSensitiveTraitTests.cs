using System.Reflection;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests;

/// <summary>
///     Every test of this assembly that measures wall-clock time or puts the machine under load (trait
///     <c>Category=Performance</c> or <c>Category=Stress</c>) also carries <c>Category=LoadSensitive</c>, so the local
///     full suite (<c>--filter "Category!=LoadSensitive"</c>, see <c>AGENTS.md</c>) leaves them out and stays
///     deterministic on a busy machine. Without this check a new budget test with only <c>Category=Performance</c>
///     would silently join the local full suite again.
/// </summary>
public class LoadSensitiveTraitTests
{
    private const string Category = "Category";
    private const string LoadSensitive = "LoadSensitive";
    private static readonly string[] LoadCategories = ["Performance", "Stress"];

    [Fact]
    public void EveryPerformanceAndStressTest_IsAlsoLoadSensitive()
    {
        var tests = TestMethods().ToList();
        var missing = tests
            .Where(t => t.Categories.Overlaps(LoadCategories) && !t.Categories.Contains(LoadSensitive))
            .Select(t => t.Name)
            .ToList();

        Assert.True(tests.Exists(t => t.Categories.Contains(LoadSensitive)),
            "positive control: the assembly holds load-sensitive tests, so the scan sees the traits");
        Assert.True(missing.Count == 0,
            "tests with Category=Performance or Category=Stress need " +
            $"[Trait(\"{Category}\", \"{LoadSensitive}\")] too: " +
            string.Join(", ", missing));
    }

    /// <summary>
    ///     The statement budget split: the recompute under a one-second budget per statement depends on the speed
    ///     of the machine and is load-sensitive; the timeout / cancellation classification runs a statement for many
    ///     times the budget and stays in the local full suite.
    /// </summary>
    [Fact]
    public void TheStatementBudgetRecompute_IsLoadSensitive_AndTheTimeoutClassification_IsNot()
    {
        var tests = TestMethods().ToDictionary(t => t.Name, t => t.Categories);

        Assert.Contains(LoadSensitive,
            tests["RollupRecomputeStatementBudgetTests.ARecomputeOfALargeRange_StaysInsideTheStatementBudget"]);
        Assert.DoesNotContain(LoadSensitive, tests["RollupRecomputeStatementBudgetTests." +
                                                   "AStatementOverItsBudget_SurfacesAsStatementTimeout_" +
                                                   "AndACancelledTokenAsCancellation"]);
    }

    private static IEnumerable<(string Name, HashSet<string> Categories)> TestMethods()
    {
        foreach (var type in typeof(LoadSensitiveTraitTests).Assembly.GetTypes())
        {
            var classCategories = CategoriesOf(type);
            // Inherited test methods included: a derived class with Category=Performance runs its base class's facts.
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<FactAttribute>(inherit: true) == null)
                {
                    continue;
                }

                var categories = new HashSet<string>(classCategories, StringComparer.Ordinal);
                categories.UnionWith(CategoriesOf(method));
                yield return ($"{type.Name}.{method.Name}", categories);
            }
        }
    }

    private static IEnumerable<string> CategoriesOf(MemberInfo member) =>
        member.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Where(t => t.Name == Category)
            .Select(t => t.Value);
}

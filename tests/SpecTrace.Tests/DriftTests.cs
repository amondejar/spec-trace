using System.Linq;
using SpecTrace.Specs;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.4: spec changes after the code and tests were reviewed (spectrace.lock, ST0010/ST0011).</summary>
    public class DriftTests
    {
        private const string Spec = @"# Orders

## ORD-001: Create order
When the customer submits a cart, the system shall create an order.

- AC-1: Given 2 lines, then the order has 2 lines.
- AC-2: Given an empty cart, then it is rejected.

## ORD-002: Cancel order
While an order is Pending, when it is cancelled, the system shall mark it Cancelled.

- AC-1: Given a Pending order, then it is Cancelled.
";

        private const string Implementation = @"
using SpecTrace;
public class OrderService
{
    [Implements(""ORD-001"")] public void Create() { }
    [Implements(""ORD-002"")] public void Cancel() { }
}";

        private const string Tests = @"
using SpecTrace;
public class OrderTests
{
    [Verifies(""ORD-001"", ""AC-1"")] public void Two_lines() { }
    [Verifies(""ORD-001"", ""AC-2"")] public void Empty_cart_rejected() { }
    [Verifies(""ORD-002"", ""AC-1"")] public void Cancels() { }
}";

        private static string LockOf(string spec) =>
            new SpecLock(SpecParser.Parse("s.spec.md", spec).Requirements.Select(SpecFingerprint.Entry)).Render();

        [Fact]
        public void Fingerprint_ignores_whitespace_but_not_words()
        {
            Assert.Equal(SpecFingerprint.Of("the  system\nshall create"), SpecFingerprint.Of(" the system shall create "));
            Assert.False(SpecFingerprint.Of("then it is rejected.") == SpecFingerprint.Of("then it is rejected with an error."));
            Assert.Equal(12, SpecFingerprint.Of("x").Length);
        }

        [Fact]
        public void Lock_round_trips_and_ignores_comments_and_garbage()
        {
            var original = LockOf(Spec);
            var parsed = SpecLock.Parse("# comment\r\n\r\ngarbage without a colon\r\n" + original.Replace("\n", "\r\n"));

            Assert.Equal(2, parsed.Count);
            Assert.Equal(original, parsed.Render());
            Assert.Equal(new[] { "AC-1", "AC-2" }, parsed.Find("ord-001")!.Criteria.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void Nothing_is_reported_when_the_spec_matches_the_lock()
        {
            var impl = TestHarness.RunWithLock(Implementation, Spec, LockOf(Spec));
            var tests = TestHarness.RunWithLock(Tests, Spec, LockOf(Spec), isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            Assert.Empty(impl.SpecTrace());
            Assert.Empty(tests.SpecTrace());
        }

        [Fact]
        public void Changed_statement_reports_ST0010_in_the_implementation_project()
        {
            var changed = Spec.Replace("the system shall create an order.", "the system shall create an order and email a receipt.");

            var impl = TestHarness.RunWithLock(Implementation, changed, LockOf(Spec));

            var d = Assert.Single(impl.SpecTrace("ST0010"));
            Assert.Contains("'ORD-001'", d.GetMessage());
            Assert.Contains("OrderService.Create", d.GetMessage());
            Assert.Contains("dotnet spectrace lock --requirement ORD-001", d.GetMessage());
            Assert.Equal(2, d.Location.GetLineSpan().StartLinePosition.Line);
        }

        [Fact]
        public void Changed_criterion_reports_ST0011_in_the_test_project_naming_the_tests()
        {
            var changed = Spec.Replace("then it is rejected.", "then it is rejected with an error.");
            var impl = TestHarness.RunWithLock(Implementation, changed, LockOf(Spec));

            var tests = TestHarness.RunWithLock(Tests, changed, LockOf(Spec), isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            Assert.Empty(impl.SpecTrace("ST0010"));
            var d = Assert.Single(tests.SpecTrace("ST0011"));
            Assert.Contains("Criterion AC-2 of 'ORD-001'", d.GetMessage());
            Assert.Contains("OrderTests.Empty_cart_rejected", d.GetMessage());
            Assert.Equal(6, d.Location.GetLineSpan().StartLinePosition.Line);
        }

        [Fact]
        public void Changes_to_title_or_status_are_not_drift()
        {
            var changed = Spec.Replace("## ORD-001: Create order", "## ORD-001: Create a new order\nStatus: implemented");

            var impl = TestHarness.RunWithLock(Implementation, changed, LockOf(Spec));

            Assert.Empty(impl.SpecTrace());
        }

        [Fact]
        public void Requirements_missing_from_the_lock_are_not_reported()
        {
            var lockOnlyOrd2 = new SpecLock(SpecParser.Parse("s.spec.md", Spec).Requirements
                .Where(r => r.Id == "ORD-002").Select(SpecFingerprint.Entry)).Render();
            var changed = Spec.Replace("the system shall create an order.", "the system shall create two orders.");

            var impl = TestHarness.RunWithLock(Implementation, changed, lockOnlyOrd2);

            Assert.Empty(impl.SpecTrace("ST0010"));
        }

        [Fact]
        public void New_criterion_is_not_drift_but_still_needs_a_test()
        {
            var changed = Spec.Replace("- AC-1: Given a Pending order, then it is Cancelled.",
                "- AC-1: Given a Pending order, then it is Cancelled.\n- AC-2: Given a Shipped order, then it is rejected.");
            var impl = TestHarness.RunWithLock(Implementation, changed, LockOf(Spec));

            var tests = TestHarness.RunWithLock(Tests, changed, LockOf(Spec), isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            Assert.Empty(tests.SpecTrace("ST0011"));
            Assert.Contains(tests.SpecTrace("ST0005"), d => d.GetMessage().Contains("AC-2") && d.GetMessage().Contains("ORD-002"));
        }
    }
}

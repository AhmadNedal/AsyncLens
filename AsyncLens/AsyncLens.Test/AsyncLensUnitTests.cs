
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using VerifyCS = AsyncLens.Test.CSharpCodeFixVerifier<
    AsyncLens.AsyncLensAnalyzer,
    AsyncLens.AsyncLensCodeFixProvider>;

namespace AsyncLens.Test
{
    [TestClass]
    public class AsyncLensUnitTest
    {
        [TestMethod]
        public async Task NoWarning_WhenTaskIsAwaited()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public async Task<int> GetDataAsync()
    {
        return await Task.FromResult(10);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task Warning_WhenUsingTaskResult()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public void Test()
    {
        var result = GetDataAsync().{|#0:Result|};
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_WhenUsingTaskWait()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync()
    {
        return Task.CompletedTask;
    }

    public void Test()
    {
        GetDataAsync().{|#0:Wait|}();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_WhenUsingGetAwaiterGetResult()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public void Test()
    {
        var result = GetDataAsync()
            .GetAwaiter()
            .{|#0:GetResult|}();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }
    }
}

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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task CodeFix_ReplacesTaskResult_WhenEnclosingAsync()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var result = GetDataAsync().{|#0:Result|};
    }
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var result = await GetDataAsync();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task CodeFix_ReplacesTaskWait_WhenEnclosingAsync()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        GetDataAsync().{|#0:Wait|}();
    }
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        await GetDataAsync();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task NoWarning_WhenUnrelatedTypeHasResultProperty()
        {
            var test = @"
class Foo { public int Result { get; set; } public void Wait() { } }

class TestClass
{
    public void Test()
    {
        var f = new Foo();
        var r = f.Result;
        f.Wait();
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        // Helper: the diagnostic must remain and the code must stay unchanged (no fix offered).
        private static async Task VerifyNoFixAsync(string sourceWithMarkup)
        {
            var t = new VerifyCS.Test
            {
                TestCode = sourceWithMarkup,
                FixedCode = sourceWithMarkup,
            };
            await t.RunAsync();
        }


        [TestMethod]
        public async Task CodeFix_MakesVoidMethodAsyncTask()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var result = GetDataAsync().{|#0:Result|};
    }
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task Test()
    {
        var result = await GetDataAsync();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task CodeFix_MakesTaskMethodAsync_AndAwaitsReturn()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public Task<int> Test()
    {
        var x = GetDataAsync().{|#0:Result|};
        return Task.FromResult(x);
    }
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task<int> Test()
    {
        var x = await GetDataAsync();
        return await Task.FromResult(x);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task CodeFix_ChangesIntReturnToTaskOfInt()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    private int Test()
    {
        return GetDataAsync().{|#0:Result|};
    }
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    private async Task<int> Test()
    {
        return await GetDataAsync();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task NoCodeFix_WhenMethodHasOutParameter()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test(out int value)
    {
        value = GetDataAsync().{|ASYNC001:Result|};
    }
}");
        }

        [TestMethod]
        public async Task NoCodeFix_WhenTaskMethodReturnsNull()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public Task<string> Test(bool b)
    {
        var x = GetDataAsync().{|ASYNC001:Result|};
        if (b) return null;
        return Task.FromResult(""a"");
    }
}");
        }


        [TestMethod]
        public async Task NoCodeFix_WhenInsideLock()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    private readonly object _gate = new object();
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        lock (_gate)
        {
            var result = GetDataAsync().{|ASYNC001:Result|};
        }
    }
}");
        }

        [TestMethod]
        public async Task NoCodeFix_WhenInsideNonAsyncLambdaWithinAsyncMethod()
        {
            await VerifyNoFixAsync(@"
using System;
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        Action a = () => { GetDataAsync().{|ASYNC001:Wait|}(); };
        a();
    }
}");
        }

        [TestMethod]
        public async Task NoCodeFix_ForWaitWithTimeout()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        GetDataAsync().{|ASYNC001:Wait|}(1000);
    }
}");
        }



        [TestMethod]
        public async Task NoWarning_ForCustomGetAwaiterAndGetResult()
        {
            var test = @"
class MyAwaiter { public int GetResult() { return 1; } }
class MyAwaitable { public MyAwaiter GetAwaiter() { return new MyAwaiter(); } }

class TestClass
{
    public void Test()
    {
        var r = new MyAwaitable().GetAwaiter().GetResult();
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }



        [TestMethod]
        public async Task Warning_WhenUsingTaskWaitWithTimeout()
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
        GetDataAsync().{|#0:Wait|}(1000);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }



        [TestMethod]
        public async Task NoWarning_WhenUsingUnrelatedWaitWithArguments()
        {
            var test = @"
class Foo
{
    public bool Wait(int timeout)
    {
        return true;
    }
}

class TestClass
{
    public void Test()
    {
        var foo = new Foo();
        var result = foo.Wait(1000);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }



        [TestMethod]
        public async Task NoWarning_WhenUsingCustomGetResult()
        {
            var test = @"
class CustomAwaiter
{
    public int GetResult()
    {
        return 10;
    }
}

class TestClass
{
    public void Test()
    {
        var awaiter = new CustomAwaiter();
        var result = awaiter.GetResult();
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }



        [TestMethod]
        public async Task NoWarning_WhenResultIsNotTaskResult()
        {
            var test = @"
class Response
{
    public int Result { get; set; }
}

class TestClass
{
    public void Test()
    {
        var response = new Response();
        var result = response.Result;
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task CodeFix_ReplacesGetAwaiterGetResult_WhenEnclosingAsync()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var result = GetDataAsync()
            .GetAwaiter()
            .{|#0:GetResult|}();
    }
}";

            var fixedCode = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var result = await GetDataAsync();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode);
        }





        [TestMethod]
        public async Task CodeFix_PreservesMemberAccess_WhenReplacingTaskResult()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var text = GetDataAsync().{|#0:Result|}.ToString();
    }
}";

            var fixedCode = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync()
    {
        return Task.FromResult(10);
    }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var text = (await GetDataAsync()).ToString();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001")
                .WithLocation(0);

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode);
        }




    }
}
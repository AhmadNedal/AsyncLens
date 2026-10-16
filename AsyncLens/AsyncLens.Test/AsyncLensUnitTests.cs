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

        // ============================================================ existing tests

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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
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

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode);
        }

        // ============================================================ new: ASYNC001 coverage

        [TestMethod]
        public async Task Warning_WhenUsingConfigureAwaitGetAwaiterGetResult()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var r = GetDataAsync().ConfigureAwait(false).GetAwaiter().{|#0:GetResult|}();
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task CodeFix_ReplacesConfigureAwaitGetResult_WhenEnclosingAsync()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        var result = GetDataAsync().ConfigureAwait(false).GetAwaiter().{|#0:GetResult|}();
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
        var result = await GetDataAsync().ConfigureAwait(false);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task Warning_WhenUsingTaskWaitAll()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        Task.{|#0:WaitAll|}(GetDataAsync(), GetDataAsync());
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_WhenUsingTaskWaitAny()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var i = Task.{|#0:WaitAny|}(GetDataAsync());
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task NoCodeFix_ForWaitAll()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        await Task.Delay(1);
        Task.{|ASYNC001:WaitAll|}(GetDataAsync());
    }
}");
        }

        [TestMethod]
        public async Task Warning_WhenUsingWaitWithTimeSpan()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public void Test()
    {
        GetDataAsync().{|#0:Wait|}(System.TimeSpan.FromSeconds(1));
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_WhenUsingWaitWithCancellationToken()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;

class TestClass
{
    public Task GetDataAsync() { return Task.CompletedTask; }

    public void Test()
    {
        GetDataAsync().{|#0:Wait|}(1000, CancellationToken.None);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_WhenUsingTaskRunResult()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public void Test()
    {
        var r = Task.Run(() => 1).{|#0:Result|};
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Warning_ReportsEachBlockingCall_InSameMethod()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var r = GetDataAsync().{|#0:Result|};
        GetDataAsync().{|#1:Wait|}();
    }
}";

            var expected0 = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            var expected1 = VerifyCS.Diagnostic("ASYNC001").WithLocation(1);
            await VerifyCS.VerifyAnalyzerAsync(test, expected0, expected1);
        }

        [TestMethod]
        public async Task NoWarning_ForGeneratedCode()
        {
            var test = @"// <auto-generated />
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var r = GetDataAsync().Result;
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        // ============================================================ new: already-completed tasks

        [TestMethod]
        public async Task NoWarning_WhenTaskIsAlreadyCompleted_FromResultAndCompletedTask()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public void Test()
    {
        var r = Task.FromResult(1).Result;
        Task.CompletedTask.Wait();
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task NoWarning_WhenTaskWasAwaitedEarlier()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        var t = GetDataAsync();
        await t;
        var r = t.Result;
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task NoWarning_AfterAwaitTaskWhenAll()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync()
    {
        var a = GetDataAsync();
        var b = GetDataAsync();
        await Task.WhenAll(a, b);
        var sum = a.Result + b.Result;
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task Warning_WhenTaskIsAwaitedOnlyInsideBranch()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public async Task TestAsync(bool flag)
    {
        var t = GetDataAsync();
        if (flag)
        {
            await t;
        }
        var r = t.{|#0:Result|};
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task NoWarning_InsideContinueWith()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        GetDataAsync().ContinueWith(t => t.Result);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        // ============================================================ new: code fix safety

        [TestMethod]
        public async Task CodeFix_ConvertsExpressionBodiedMember()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    private int Test() => GetDataAsync().{|#0:Result|};
}";

            var fixtest = @"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    private async Task<int> Test() => await GetDataAsync();
}";

            var expected = VerifyCS.Diagnostic("ASYNC001").WithLocation(0);
            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        }

        [TestMethod]
        public async Task NoCodeFix_WhenVoidMethodHasCallers()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    private void Test()
    {
        var r = GetDataAsync().{|ASYNC001:Result|};
    }

    public void Caller()
    {
        Test();
    }
}");
        }

        [TestMethod]
        public async Task NoCodeFix_WhenVoidMethodIsPubliclyVisible()
        {
            await VerifyNoFixAsync(@"
using System.Threading.Tasks;

public class TestClass
{
    public Task<int> GetDataAsync() { return Task.FromResult(10); }

    public void Test()
    {
        var r = GetDataAsync().{|ASYNC001:Result|};
    }
}");
        }

        // ============================================================ new: ASYNC002 (Thread.Sleep)

        [TestMethod]
        public async Task Warning_WhenThreadSleepInsideAsyncMethod()
        {
            var test = @"
using System.Threading;
using System.Threading.Tasks;

class TestClass
{
    public async Task TestAsync()
    {
        await Task.Delay(1);
        Thread.{|#0:Sleep|}(1000);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC002").WithLocation(0);
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        //        [TestMethod]
        //        public async Task CodeFix_ReplacesThreadSleepWithTaskDelay()
        //        {
        //            var test = @"
        //using System.Threading;
        //using System.Threading.Tasks;

        //class TestClass
        //{
        //    public async Task TestAsync()
        //    {
        //        await Task.Delay(1);
        //        Thread.{|#0:Sleep|}(1000);
        //    }
        //}";

        //            var fixtest = @"
        //using System.Threading;
        //using System.Threading.Tasks;

        //class TestClass
        //{
        //    public async Task TestAsync()
        //    {
        //        await Task.Delay(1);
        //        await Task.Delay(1000);
        //    }
        //}";

        //            var expected = VerifyCS.Diagnostic("ASYNC002").WithLocation(0);
        //            await VerifyCS.VerifyCodeFixAsync(test, expected, fixtest);
        //        }

        [TestMethod]
        public async Task NoWarning_WhenThreadSleepInsideNonAsyncMethod()
        {
            var test = @"
using System.Threading;

class TestClass
{
    public void Test()
    {
        Thread.Sleep(1000);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task NoWarning_WhenThreadSleepInsideNonAsyncLambda()
        {
            var test = @"
using System;
using System.Threading;
using System.Threading.Tasks;

class TestClass
{
    public async Task TestAsync()
    {
        await Task.Delay(1);
        Action a = () => Thread.Sleep(10);
        a();
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        // ============================================================ new: ASYNC003 (async void)

        [TestMethod]
        public async Task Warning_WhenAsyncVoidMethod()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public async void {|#0:Foo|}()
    {
        await Task.Delay(1);
    }
}";

            var expected = VerifyCS.Diagnostic("ASYNC003").WithLocation(0).WithArguments("Foo");
            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task NoWarning_WhenAsyncVoidIsEventHandler()
        {
            var test = @"
using System;
using System.Threading.Tasks;

class TestClass
{
    public async void OnClick(object sender, EventArgs e)
    {
        await Task.Delay(1);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task NoWarning_WhenAsyncTaskMethod()
        {
            var test = @"
using System.Threading.Tasks;

class TestClass
{
    public async Task Foo()
    {
        await Task.Delay(1);
    }
}";

            await VerifyCS.VerifyAnalyzerAsync(test);
        }
    }
}
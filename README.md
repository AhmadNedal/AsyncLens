# BlockingAsync.Analyzer

Roslyn analyzer that detects blocking on Tasks.

## Diagnostics
- **ASYNC001**: Avoid blocking on asynchronous code
  (`Task<T>.Result`, `Task.Wait(...)`, `Task.GetAwaiter().GetResult()`)

## Code fix
Replaces the blocking call with `await` when the enclosing method is async,
or offers to make the method async.

## Known limitations
- `ConfigureAwait(false).GetAwaiter().GetResult()` and `ValueTask` are not detected yet.
- Fix All may behave unexpectedly when several diagnostics are in one method.
- Converting a `void` method to `Task` does not update its callers.
- Replacing blocking calls with `await` changes exception behavior (no `AggregateException`).

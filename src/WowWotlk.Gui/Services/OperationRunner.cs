using Microsoft.Extensions.DependencyInjection;

namespace WowWotlk.Gui.Services;

public enum OperationOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

public sealed record OperationResult(OperationOutcome Outcome, Exception? Error = null);

/// <summary>
/// Runs one operation at a time — an install, an addon install and a Steam setup all write to
/// the same client tree, so they must never overlap. Each run gets a fresh DI scope and a
/// per-operation CancellationTokenSource.
/// </summary>
public class OperationRunner(IServiceProvider serviceProvider, LogService log)
{
    public bool IsBusy => _busy == 1;
    public string? CurrentOperation { get; private set; }

    public event Action<string>? Started;
    public event Action<string, OperationResult>? Completed;

    public async Task<OperationResult> RunAsync(
        string name,
        Func<IServiceProvider, CancellationToken, Task> work
    )
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // Reported, not just returned. This path is reached by pressing a button whose
            // command was enabled, and returning quietly means the button visibly does
            // nothing — no status line, no log entry, no clue that anything happened.
            var rejected = new OperationResult(
                OperationOutcome.Failed,
                new InvalidOperationException($"Another operation is running: {CurrentOperation}")
            );
            log.Append($"{name}: not started — {CurrentOperation} is still running.");
            try
            {
                Completed?.Invoke(name, rejected);
            }
            catch (Exception e)
            {
                log.Append($"{name}: a completion handler failed — {e.Message}");
            }
            return rejected;
        }

        OperationResult result;
        try
        {
            CurrentOperation = name;
            _cts = new CancellationTokenSource();
            // Inside the try on purpose. Both of these dispatch to subscriber code — the view
            // models post to the dispatcher, which throws once it has shut down — and a throw
            // out here would leave the busy flag set with no finally to clear it. Every later
            // operation for the life of the process would then refuse to start, and only a
            // restart would fix it.
            Started?.Invoke(name);
            log.Append($"{name}: started");
            using var scope = serviceProvider.CreateScope();
            await Task.Run(() => work(scope.ServiceProvider, _cts.Token), _cts.Token);
            result = new OperationResult(OperationOutcome.Succeeded);
            log.Append($"{name}: finished");
        }
        catch (OperationCanceledException)
        {
            result = new OperationResult(OperationOutcome.Cancelled);
            log.Append($"{name}: cancelled");
        }
        catch (Exception) when (_cts?.IsCancellationRequested == true)
        {
            // A killed subprocess or an aborted download surfaces as an arbitrary exception
            // type, so go by the token, not the exception.
            result = new OperationResult(OperationOutcome.Cancelled);
            log.Append($"{name}: cancelled");
        }
        catch (Exception e)
        {
            result = new OperationResult(OperationOutcome.Failed, e);
            log.Append($"{name}: FAILED — {e.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CurrentOperation = null;
            Interlocked.Exchange(ref _busy, 0);
        }
        // Guarded for the same reason as Started: RunAsync's contract is to report a failure
        // as a result, never to throw. An exception escaping here reaches an async command
        // with nothing to catch it, which takes the process down.
        try
        {
            Completed?.Invoke(name, result);
        }
        catch (Exception e)
        {
            log.Append($"{name}: a completion handler failed — {e.Message}");
        }
        return result;
    }

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced the finally block disposing the CTS as the run wound down; the operation
            // is over either way, so a late Cancel click is a no-op.
        }
    }

    private int _busy;
    private CancellationTokenSource? _cts;
}

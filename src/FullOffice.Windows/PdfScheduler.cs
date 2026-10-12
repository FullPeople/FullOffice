namespace FlowOfficeLab;

// Native operations share one worker; interaction takes priority over prefetch.
internal static class PdfScheduler
{
    interface IWork { void Execute(); }
    sealed class Work<T> : IWork
    {
        readonly Func<T> action;
        readonly CancellationToken token;
        readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration registration;
        public Task<T> Task => completion.Task;
        public Work(Func<T> action, CancellationToken token)
        {
            this.action = action; this.token = token;
            registration = token.Register(() => completion.TrySetCanceled(token));
        }
        public void Execute()
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { registration.Dispose(); }
        }
    }
    static readonly object sync = new();
    static readonly Queue<IWork> interactive = new(), speculative = new();
    static PdfScheduler()
    {
        new Thread(() => {
            while (true) {
                IWork work;
                lock (sync) {
                    while (interactive.Count == 0 && speculative.Count == 0) Monitor.Wait(sync);
                    work = interactive.Count > 0 ? interactive.Dequeue() : speculative.Dequeue();
                }
                work.Execute();
            }
        }) { IsBackground = true, Name = "FullOffice PDF worker" }.Start();
    }
    public static Task<T> Run<T>(Func<T> action, CancellationToken token = default, bool background = false)
    {
        var work = new Work<T>(action, token);
        lock (sync) { (background ? speculative : interactive).Enqueue(work); Monitor.Pulse(sync); }
        return work.Task;
    }
    public static Task Run(Action action, CancellationToken token = default, bool background = false)
        => Run(() => { action(); return true; }, token, background);
}

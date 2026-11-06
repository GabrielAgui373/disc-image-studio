using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Results;

namespace DiscImageStudio.Application.Jobs;

public sealed record JobRequest(string Name, bool Exclusive = true);

public sealed record JobProgress(string Stage, string? Message = null, double? Fraction = null);

public sealed record JobExecutionContext(
    Guid JobId,
    IProgress<JobProgress>? Progress,
    CancellationToken CancellationToken);

public sealed record JobCompletion(Guid JobId, string Name, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc);

public sealed record JobStarted(Guid JobId, string Name);

public sealed record JobProgressReported(Guid JobId, JobProgress Progress);

public sealed record JobFinished(Guid JobId, string Name, ApplicationErrorCode? ErrorCode);

/// <summary>
/// Coordinates portable asynchronous work. State is protected by a lock; operations run on
/// their caller-selected scheduler and progress callbacks use the supplied IProgress policy.
/// </summary>
public sealed class JobCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunningJob> _running = [];
    private readonly IClock _clock;
    private readonly IApplicationEventPublisher _events;
    private bool _acceptingJobs = true;

    public JobCoordinator(IClock clock, IApplicationEventPublisher? events = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _events = events ?? new NullApplicationEventPublisher();
    }

    public IReadOnlyList<Guid> RunningJobIds
    {
        get
        {
            lock (_gate)
            {
                return _running.Keys.ToArray();
            }
        }
    }

    public bool IsAcceptingJobs
    {
        get
        {
            lock (_gate)
            {
                return _acceptingJobs;
            }
        }
    }

    public async Task<ApplicationResult<JobCompletion>> StartJobAsync(
        JobRequest request,
        Func<JobExecutionContext, Task> operation,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ApplicationResult<JobCompletion>.Failure(ApplicationErrorCode.InvalidRequest, "Job name is required.");
        }

        Guid jobId = Guid.NewGuid();
        DateTimeOffset startedUtc = _clock.UtcNow;
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_acceptingJobs)
            {
                linked.Dispose();
                return ApplicationResult<JobCompletion>.Failure(
                    ApplicationErrorCode.ApplicationShuttingDown,
                    request.Name);
            }

            if (HasConflict(request))
            {
                linked.Dispose();
                return ApplicationResult<JobCompletion>.Failure(ApplicationErrorCode.JobConflict, request.Name);
            }

            _running.Add(jobId, new RunningJob(request, linked, completion));
        }

        _events.Publish(new JobStarted(jobId, request.Name));
        ApplicationErrorCode? errorCode = null;
        try
        {
            IProgress<JobProgress> reportedProgress = new Progress<JobProgress>(update =>
            {
                progress?.Report(update);
                _events.Publish(new JobProgressReported(jobId, update));
            });

            await operation(new JobExecutionContext(jobId, reportedProgress, linked.Token));
            return ApplicationResult<JobCompletion>.Success(
                new JobCompletion(jobId, request.Name, startedUtc, _clock.UtcNow));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            errorCode = ApplicationErrorCode.JobCanceled;
            return ApplicationResult<JobCompletion>.Failure(errorCode.Value, request.Name);
        }
        catch (Exception exception)
        {
            errorCode = ApplicationErrorCode.JobFailed;
            return ApplicationResult<JobCompletion>.Failure(errorCode.Value, exception.Message, exception);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(jobId);
            }

            linked.Dispose();
            completion.TrySetResult();
            _events.Publish(new JobFinished(jobId, request.Name, errorCode));
        }
    }

    public ApplicationResult CancelJob(Guid jobId)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (!_running.TryGetValue(jobId, out RunningJob? running))
            {
                return ApplicationResult.Failure(ApplicationErrorCode.JobNotFound, jobId.ToString());
            }

            cancellation = running.Cancellation;
        }

        try
        {
            // Cancellation callbacks run outside the coordinator lock. A callback may safely
            // inspect job state or publish progress without creating a lock inversion.
            cancellation.Cancel();
            return ApplicationResult.Success();
        }
        catch (ObjectDisposedException)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.JobNotFound, jobId.ToString());
        }
    }

    public async Task<bool> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        RunningJob[] running;
        lock (_gate)
        {
            _acceptingJobs = false;
            running = _running.Values.ToArray();
        }

        foreach (RunningJob job in running)
        {
            try
            {
                job.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            await Task.WhenAll(running.Select(job => job.Completion.Task))
                .WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private bool HasConflict(JobRequest candidate)
        => _running.Values.Any(running => candidate.Exclusive || running.Request.Exclusive);

    private sealed record RunningJob(
        JobRequest Request,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Completion);
}

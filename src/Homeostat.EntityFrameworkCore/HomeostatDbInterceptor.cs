using System.Data.Common;
using Homeostat.Calls;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Homeostat.EntityFrameworkCore;

/// <summary>
/// Records every command of a <c>DbContext</c> in a <see cref="CallRecorder"/>: executed commands as successes with
/// their duration, failed ones according to <see cref="DbContextSignalOptions.IsFailure"/>, and connections that cannot
/// be opened (for example an exhausted pool) or closed as failures. Canceled commands and connections are not recorded
/// (EF Core reports them as canceled, not as failed).
/// </summary>
internal sealed class HomeostatDbInterceptor(CallRecorder recorder, Func<Exception, bool> isFailure) : IDbCommandInterceptor, IDbConnectionInterceptor
{
    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        recorder.Record(false, eventData.Duration);
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        recorder.Record(false, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        recorder.Record(false, eventData.Duration);
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        recorder.Record(false, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        recorder.Record(false, eventData.Duration);
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        recorder.Record(false, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => recorder.Record(IsFailure(eventData.Exception), eventData.Duration);

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        recorder.Record(IsFailure(eventData.Exception), eventData.Duration);
        return Task.CompletedTask;
    }

    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
        => recorder.Record(true, eventData.Duration);

    public Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        recorder.Record(true, eventData.Duration);
        return Task.CompletedTask;
    }

    // A classifier that throws must not replace the command's exception in the application: it counts as a failure.
    private bool IsFailure(Exception exception)
    {
        try
        {
            return isFailure(exception);
        }
        catch
        {
            return true;
        }
    }
}

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Homeostat.Healing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat;

/// <summary>Database healing actions.</summary>
public static class HealingPlanBuilderDataExtensions
{
    /// <summary>
    /// Appends a step that empties every connection pool of the ADO.NET provider <typeparamref name="TConnection"/>,
    /// by calling its static <c>ClearAllPools()</c> method (or <c>ClearAllPoolsAsync</c>). Use it after a failover or a
    /// network change, when pooled connections point to a server that no longer answers: new connections are opened
    /// on demand. Supported by SqlClient (<c>SqlConnection</c>), Npgsql (<c>NpgsqlConnection</c>), MySqlConnector and
    /// Microsoft.Data.Sqlite (<c>SqliteConnection</c>), among others.
    /// </summary>
    /// <typeparam name="TConnection">The provider's connection type.</typeparam>
    /// <param name="plan">The plan builder.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TConnection"/> has no public static ClearAllPools method.</exception>
    /// <example><code>.Heal(p => p.With&lt;FailoverProbe&gt;().ClearConnectionPools&lt;SqlConnection&gt;())</code></example>
    public static HealingPlanBuilder ClearConnectionPools<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] TConnection>(
        this HealingPlanBuilder plan)
        where TConnection : DbConnection
    {
        ArgumentNullException.ThrowIfNull(plan);
        var clear = FindClearAllPools(typeof(TConnection));
        var name = $"Clear {typeof(TConnection).Name} pools";

        return plan.With(name, async (context, ct) =>
        {
            context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Data")
                .LogWarning("Clearing all {Provider} connection pools for condition {Condition}.", typeof(TConnection).Name, context.ConditionName);
            await clear(ct).ConfigureAwait(false);
        });
    }

    internal static Func<CancellationToken, Task> FindClearAllPools(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type connectionType)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

        if (connectionType.GetMethod("ClearAllPools", flags, Type.EmptyTypes) is { } sync)
        {
            return _ =>
            {
                Invoke(sync, []);
                return Task.CompletedTask;
            };
        }

        if (connectionType.GetMethod("ClearAllPoolsAsync", flags, [typeof(CancellationToken)]) is { } withToken)
        {
            return ct => AwaitResult(Invoke(withToken, [ct]));
        }

        if (connectionType.GetMethod("ClearAllPoolsAsync", flags, Type.EmptyTypes) is { } withoutToken)
        {
            return _ => AwaitResult(Invoke(withoutToken, []));
        }

        throw new InvalidOperationException(
            $"{connectionType.FullName} has no public static ClearAllPools() or ClearAllPoolsAsync() method, so its pools cannot be cleared.");
    }

    private static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    private static Task AwaitResult(object? result) => result switch
    {
        Task task => task,
        ValueTask valueTask => valueTask.AsTask(),
        _ => Task.CompletedTask,
    };
}

namespace Homeostat.Persistence;

/// <summary>Helpers for the default implementations of <see cref="IHomeostatStore"/>.</summary>
internal static class StoreDefaults
{
    public static async ValueTask<bool> ThenTrue(ValueTask write)
    {
        await write.ConfigureAwait(false);
        return true;
    }
}

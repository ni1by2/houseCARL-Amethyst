using Microsoft.Data.Sqlite;

namespace HousecarlMcpTests;

/// <summary>Hold a synthetic catalog exclusively so the real reader must return its bounded lock refusal.</summary>
sealed class CatalogHold : IDisposable
{
    readonly SqliteConnection _connection;
    public CatalogHold(string database)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE";
        command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
        _connection.Dispose();
    }
}

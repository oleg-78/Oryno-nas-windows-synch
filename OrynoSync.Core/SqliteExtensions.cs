using Microsoft.Data.Sqlite;
namespace OrynoSync.Core;
internal static class SqliteExtensions
{
    public static SqliteCommand CreateCommand(this SqliteConnection connection,string text){var c=connection.CreateCommand();c.CommandText=text;return c;}
}

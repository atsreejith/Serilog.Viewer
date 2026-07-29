using System.Text;
using Microsoft.Data.Sqlite;

namespace Serilog.Viewer.Infrastructure.Indexing;

/// <summary>
/// Accumulates index rows and writes them as multi-row INSERT statements.
/// <para>
/// The previous ingest issued one <c>ExecuteNonQuery</c> per log entry, so a million-entry file
/// cost a million SQLite round-trips and dominated cold-start time. Batching amortises that to
/// one round-trip per <see cref="BatchRows"/> entries.
/// </para>
/// <para>
/// The full-size command is built once and its parameters rebound per batch; only the final,
/// short batch pays for building a differently-shaped statement.
/// </para>
/// </summary>
internal sealed class EntryInsertBatch
{
    /// <summary>
    /// Rows per INSERT. SQLite's default SQLITE_MAX_VARIABLE_NUMBER is 999 on older builds, so
    /// 500 rows x 10 columns would exceed it — parameters are therefore bound positionally per
    /// row with distinct names, and 500 keeps the statement text a manageable size while
    /// capturing nearly all of the round-trip savings.
    /// </summary>
    private const int BatchRows = 500;

    private const int ColumnsPerRow = 10;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly List<EntryRow> _pending = new(BatchRows);

    private SqliteCommand? _fullCommand;

    public EntryInsertBatch(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task AddAsync(
        long fileId,
        long seq,
        long offset,
        long timestampTicks,
        int level,
        string? message,
        string? exception,
        string? sourceContext,
        string? correlationId,
        string? requestId,
        CancellationToken cancellationToken
    )
    {
        _pending.Add(
            new EntryRow(
                fileId,
                seq,
                offset,
                timestampTicks,
                level,
                message,
                exception,
                sourceContext,
                correlationId,
                requestId
            )
        );

        if (_pending.Count >= BatchRows)
            await FlushAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
            return;

        SqliteCommand command;
        if (_pending.Count == BatchRows)
        {
            _fullCommand ??= CreateCommand(BatchRows);
            command = _fullCommand;
        }
        else
        {
            command = CreateCommand(_pending.Count);
        }

        for (var i = 0; i < _pending.Count; i++)
        {
            var row = _pending[i];
            var p = command.Parameters;
            var b = i * ColumnsPerRow;
            p[b + 0].Value = row.FileId;
            p[b + 1].Value = row.Seq;
            p[b + 2].Value = row.Offset;
            p[b + 3].Value = row.TimestampTicks;
            p[b + 4].Value = row.Level;
            p[b + 5].Value = (object?)row.Message ?? DBNull.Value;
            p[b + 6].Value = (object?)row.Exception ?? DBNull.Value;
            p[b + 7].Value = (object?)row.SourceContext ?? DBNull.Value;
            p[b + 8].Value = (object?)row.CorrelationId ?? DBNull.Value;
            p[b + 9].Value = (object?)row.RequestId ?? DBNull.Value;
        }

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (!ReferenceEquals(command, _fullCommand))
                command.Dispose();
        }

        _pending.Clear();
    }

    private SqliteCommand CreateCommand(int rows)
    {
        var sql = new StringBuilder(
            """
            INSERT OR IGNORE INTO entries (
                file_id,
                seq,
                offset,
                timestamp_ticks,
                level,
                message,
                exception,
                source_context,
                correlation_id,
                request_id
            ) VALUES
            """
        );

        for (var i = 0; i < rows; i++)
        {
            sql.Append(i == 0 ? "\n(" : ",\n(");
            for (var c = 0; c < ColumnsPerRow; c++)
            {
                if (c > 0)
                    sql.Append(", ");
                sql.Append('$').Append('p').Append(i * ColumnsPerRow + c);
            }
            sql.Append(')');
        }
        sql.Append(';');

        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql.ToString();

        for (var i = 0; i < rows; i++)
        {
            var b = i * ColumnsPerRow;
            command.Parameters.Add($"$p{b + 0}", SqliteType.Integer);
            command.Parameters.Add($"$p{b + 1}", SqliteType.Integer);
            command.Parameters.Add($"$p{b + 2}", SqliteType.Integer);
            command.Parameters.Add($"$p{b + 3}", SqliteType.Integer);
            command.Parameters.Add($"$p{b + 4}", SqliteType.Integer);
            command.Parameters.Add($"$p{b + 5}", SqliteType.Text);
            command.Parameters.Add($"$p{b + 6}", SqliteType.Text);
            command.Parameters.Add($"$p{b + 7}", SqliteType.Text);
            command.Parameters.Add($"$p{b + 8}", SqliteType.Text);
            command.Parameters.Add($"$p{b + 9}", SqliteType.Text);
        }

        return command;
    }

    public void Dispose() => _fullCommand?.Dispose();

    private readonly record struct EntryRow(
        long FileId,
        long Seq,
        long Offset,
        long TimestampTicks,
        int Level,
        string? Message,
        string? Exception,
        string? SourceContext,
        string? CorrelationId,
        string? RequestId
    );
}

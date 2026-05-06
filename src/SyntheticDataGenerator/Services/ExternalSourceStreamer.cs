using Microsoft.Data.SqlClient;
using System.Data;

namespace SyntheticDataGenerator.Services;

/// <summary>
/// Streams non-null values from a single column of a database table using a
/// fixed-size rotating buffer (reservoir-style window). Designed for external
/// custom-dependency root columns that may contain billions of rows: at most
/// <c>BufferSize</c> values are ever in memory.
///
/// Lifecycle: lazy-opened on first <see cref="Pick"/> call, holds one
/// <see cref="SqlConnection"/> + <see cref="SqlDataReader"/> for the run, and is
/// released via <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ExternalSourceStreamer : IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _tableName;
    private readonly string _column;
    private readonly int _bufferSize;
    private readonly Random _random;

    private SqlConnection? _connection;
    private SqlCommand? _command;
    private SqlDataReader? _reader;

    private object[]? _buffer;
    private int _filled;
    private bool _readerExhausted;

    // Pick / EnsureOpened mutate the buffer, the reader, and _random. Held
    // for the full Pick call so concurrent callers from parallel table tasks
    // don't corrupt state or race the SqlDataReader.
    private readonly object _pickLock = new();

    public ExternalSourceStreamer(
        string connectionString,
        string fullTableName,
        string column,
        int bufferSize,
        Random? random = null)
    {
        _connectionString = connectionString;
        var dotIdx = fullTableName.IndexOf('.');
        _schema = dotIdx >= 0 ? fullTableName[..dotIdx] : "dbo";
        _tableName = dotIdx >= 0 ? fullTableName[(dotIdx + 1)..] : fullTableName;
        _column = column;
        _bufferSize = bufferSize > 0 ? bufferSize : 10_000;
        _random = random ?? new Random();
    }

    public string FullTableName => $"{_schema}.{_tableName}";
    public string Column => _column;
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Picks a value uniformly from the rotating buffer. On every call where the
    /// underlying reader still has rows, the picked slot is overwritten with the
    /// next value from the reader, advancing the window across the full result
    /// set. Once the reader is exhausted, the buffer continues to serve values
    /// without further DB I/O.
    /// </summary>
    public object Pick()
    {
        lock (_pickLock)
        {
            EnsureOpened();

            if (_buffer is null || _filled == 0)
                throw new InvalidOperationException(
                    $"External source [{_schema}].[{_tableName}].[{_column}] returned no non-null values.");

            var idx = _random.Next(_filled);
            var picked = _buffer[idx];

            if (!_readerExhausted && _reader is not null)
            {
                if (_reader.Read())
                {
                    var next = _reader.GetValue(0);
                    if (next is not DBNull)
                        _buffer[idx] = next;
                }
                else
                {
                    _readerExhausted = true;
                }
            }

            return picked;
        }
    }

    private void EnsureOpened()
    {
        if (_buffer is not null) return;

        _connection = new SqlConnection(_connectionString);
        _connection.Open();

        var sql = $"SELECT [{_column}] FROM [{_schema}].[{_tableName}] WHERE [{_column}] IS NOT NULL";
        _command = new SqlCommand(sql, _connection)
        {
            CommandTimeout = 0
        };
        _reader = _command.ExecuteReader(CommandBehavior.SequentialAccess);

        _buffer = new object[_bufferSize];
        _filled = 0;

        while (_filled < _bufferSize && _reader.Read())
        {
            var val = _reader.GetValue(0);
            if (val is DBNull) continue;
            _buffer[_filled++] = val;
        }

        if (_filled < _bufferSize)
            _readerExhausted = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            await _reader.DisposeAsync();
            _reader = null;
        }
        if (_command is not null)
        {
            await _command.DisposeAsync();
            _command = null;
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _buffer = null;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _command?.Dispose();
        _connection?.Dispose();
        _reader = null;
        _command = null;
        _connection = null;
        _buffer = null;
    }
}

using System.Data;
using System.Runtime.Serialization;
using System.Text.Json;
using EventStorage.AggregateRoot;
using EventStorage.Events;
using EventStorage.Extensions;
using EventStorage.Projections;
using EventStorage.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EventStorage.Infrastructure;

public class SqlServerClient<T>(IServiceProvider sp, string conn) : ClientBase<T>(sp) where T : IEventSource
{
    private readonly SemaphoreSlim _semaphore = new (1, 1);
    private readonly ILogger logger = TLogger.Create<SqlServerClient<T>>();
    public override async Task InitSource()
    {
        try
        {
            logger.LogInformation($"Begin initializing {nameof(SqlServerClient<T>)}.");
            _semaphore.Wait();
            await using SqlConnection sqlConnection = new(conn);
            await sqlConnection.OpenAsync();
            await using SqlTransaction sqlTransaction = sqlConnection.BeginTransaction();
            await using SqlCommand command = new(Schema.CreateSchemaIfNotExists, sqlConnection);
            command.Transaction = sqlTransaction;
            await command.ExecuteNonQueryAsync();
            foreach (var item in TProjections(t => true))
            {
                command.CommandText = Schema.CreateProjectionIfNotExists(item?.Name?? "");
                await command.ExecuteNonQueryAsync();
            }
            command.CommandText = Schema.CreateCheckpointIfNotExists;
            await command.ExecuteNonQueryAsync();
            await sqlTransaction.CommitAsync();
            logger.LogInformation($"Finished initializing {nameof(SqlServerClient<T>)}.");
            _semaphore.Release();
        }
        catch(SqlException e)
        {
            logger.LogInformation($"Failed initializing {nameof(SqlServerClient<T>)}. {e.Message}");
            throw;
        }
    }
    public override async Task<T> CreateOrRestore(string? sourceId = null)
    {
        try
        {
            logger.Log($"Started creating or restoring {typeof(T).Name} aggregate.");
            await using SqlConnection sqlConnection = new(conn);
            await sqlConnection.OpenAsync();
            await using SqlCommand sqlCommand = sqlConnection.CreateCommand();

            IEnumerable<EventEnvelop> events = [];
            if(sourceId != null)
            {
                sqlCommand.CommandText = Schema.LoadEventSourceCommand(SourceTId.ToString());
                sqlCommand.Parameters.Add(new SqlParameter("sourceId", sourceId));
                events = await LoadEvents(() => sqlCommand);
                if(!events.Any())
                    throw new Exception("No such event source with this id exists.");
            }

            sourceId ??= await GenerateSourceId(sqlCommand);
            var aggregate = typeof(T).CreateAggregate<T>(sourceId);
            aggregate.RestoreAggregate(true, events.Select(x => x.SourcedEvent).ToArray());
            logger.LogInformation($"Finished restoring {typeof(T).Name} aggregate {sourceId}.");

            return aggregate;
        }
        catch (Exception e)
        {
            if(logger.IsEnabled(LogLevel.Error))
                logger.LogError($"Failed restoring aggregate {typeof(T).Name}. {e.Message}");
            throw;
        }
    }
    public override async Task Commit(T aggregate)
    {
        var x = aggregate.PendingEvents.Count();
        logger.Log($"Preparing to commit {x} pending event(s) for event source {LongSourceId}.");
        
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlTransaction sqlTransaction = sqlConnection.BeginTransaction();
        await using SqlCommand sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.Transaction = sqlTransaction;
        try
        {
            // check for concurrent stream access
            await CheckConcurrency(sqlCommand, new SqlParameter[]
            {
                new("sourceId" , LongSourceId),
                new ("expected", (object?)aggregate.EventStream.LastOrDefault()?.Version?? DBNull.Value)
            });

            // add event source to event storage
            if(aggregate.PendingEvents.Any())
            {
                await PrepareSourceCommand((names, values) => names.Select((x, i) => new SqlParameter
                {
                    ParameterName = x.Key,
                    SqlDbType = (SqlDbType)x.Value,
                    SqlValue = values[i]
                }).ToArray(), sqlCommand, aggregate.PendingEvents.ToArray());
            }

            // apply consistent projections if any
            var pending = aggregate.FlushPendingEvents();
            await PrepareProjectionCommand(p =>
                ProjectionRestorer.Subscribes(pending, p) && pending.Any(),
                (names, values) => names.Select((x, i) => new SqlParameter
                {
                    ParameterName = x.Key,
                    SqlDbType = (SqlDbType)x.Value,
                    SqlValue = values[i]
                }).ToArray(),
                sqlCommand, new(LongSourceId, GuidSourceId, aggregate.EventStream),
                Projections.Where(x => x.Mode == ProjectionMode.Consistent)
            );

            await sqlTransaction.CommitAsync();
            logger.LogInformation($"Committed {x} pending event(s) for event source {LongSourceId}");
            EventSourceEnvelop envelop = new(LongSourceId, GuidSourceId, pending);
            ProjectionPool.Release((ct) => envelop);
        }
        catch (Exception e)
        {
            await sqlTransaction.RollbackAsync();
            if(logger.IsEnabled(LogLevel.Error))
                logger.LogError($"Commit failure for {typeof(T).Name}. {e.Message}");
            throw;
        }
    }
    public override async Task RestoreProjection(Projection p, IServiceProvider sp, params EventSourceEnvelop[] sources)
    {
        if(p.Configuration.Store == ProjectionStore.Redis)
        {
            var redis = sp.GetRequiredService<IRedisService>();
            await redis.RestoreProjection(p, sources);
        }
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlTransaction sqlTransaction = sqlConnection.BeginTransaction();
        await using SqlCommand sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.Transaction = sqlTransaction;

        if(p.Configuration.Store == ProjectionStore.Selected)
        {
            foreach(var source in sources)
            {
                await PrepareProjectionCommand((p) => true,
                (names, values) => names.Select((x, i) => new SqlParameter
                {
                    ParameterName = x.Key,
                    SqlDbType = (SqlDbType)x.Value,
                    SqlValue = values[i]
                }).ToArray(),
                sqlCommand,
                source,
                [p], sp.GetRequiredService<IProjectionRestorer>());
            }
        }
        await sqlTransaction.CommitAsync();
    }
    public override async Task<M?> Project<M>(string sourceId) where M : class
    {
        try
        {
            logger.LogInformation($"Starting {typeof(M).Name} projection.");
            var projection = ServiceProvider.GetService<IProjection<M>>();
            if(projection == null)
                return default;
                
            if(projection.Configuration.Store == ProjectionStore.Redis)
                return await Redis.GetDocument<M>(sourceId);

            await using SqlConnection sqlConnection = new(conn);
            await sqlConnection.OpenAsync();
            await using SqlCommand command = sqlConnection.CreateCommand();

            if(projection.Mode != ProjectionMode.Transient)
            {
                command.CommandText = Schema.GetDocumentCommand<M>(SourceTId.ToString());
                command.Parameters.AddWithValue("@sourceId", sourceId);
                await using SqlDataReader reader = await command.ExecuteReaderAsync();
                if(!await reader.ReadAsync())
                    return default;
                var json = reader.GetString(EventStorageSchema.Data);
                var m = JsonSerializer.Deserialize<M>(json);
                logger.LogInformation($"{typeof(M).Name} projection completed.");
                return m;
            }

            command.CommandText = Schema.LoadEventSourceCommand(SourceTId.ToString());
            command.Parameters.Add(new SqlParameter("sourceId", sourceId));
            var events = await LoadEvents(() => command);
            var model = ProjectionRestorer.Project<M>(events.Select(x => x.SourcedEvent));
            logger.LogInformation($"{typeof(M).Name} projection completed.");
            return model;
        }
        catch (Exception e)
        {
            if(logger.IsEnabled(LogLevel.Error))
                logger.LogError($"Projection failure for {typeof(M).Name}. {e.Message}");
            throw;
        }
    }
    public override async Task<Checkpoint> LoadCheckpoint(IProjection projection)
    {
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlCommand sqlCommand = new (Schema.LoadCheckpointCommand, sqlConnection);
        sqlCommand.Parameters.AddWithValue("@subscription", projection.GetType().Name);
        sqlCommand.Parameters.AddWithValue("@type", CheckpointType.Projection);
        SqlDataReader reader = await sqlCommand.ExecuteReaderAsync();
        Checkpoint checkpoint = new(projection.GetType().Name, 0, 0);
        long seq = 0;
        if(await reader.ReadAsync())
            seq = reader.GetInt64("sequence");
        else
            await SaveCheckpoint(checkpoint, true);
        return checkpoint with { Seq = seq};
    }
    public override async Task SaveCheckpoint(Checkpoint checkpoint, bool insert = false)
    {
        try
        {
            await using SqlConnection sqlConnection = new(conn);
            await sqlConnection.OpenAsync();
            await using SqlCommand sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = insert ? Schema.InsertCheckpointCommand : Schema.SaveCheckpointCommand;
            sqlCommand.Parameters.AddWithValue("@subscription", checkpoint.Subscription);
            sqlCommand.Parameters.AddWithValue("@sequence", checkpoint.Seq);
            sqlCommand.Parameters.AddWithValue("@type", checkpoint.Type);
            await sqlCommand.ExecuteNonQueryAsync();
        }
        catch(Exception e)
        {
            if(logger.IsEnabled(LogLevel.Error))
                logger.LogError($"Failure saving checkpoint for {typeof(T).Name}. {e.Message}");
            throw;
        }
    }
    public override async Task<IEnumerable<EventEnvelop>> LoadEventsPastCheckpoint(Checkpoint c)
    {
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlCommand sqlCommand = new(Schema.LoadEventsPastCheckpoint, sqlConnection);
        sqlCommand.Parameters.AddWithValue("@seq", c.Seq);
        sqlCommand.Parameters.AddWithValue("@maxSeq", c.MaxSeq);
        var events = await LoadEvents(() => sqlCommand);
        return events;
    }
    public override async Task<IEnumerable<EventEnvelop>> LoadEventSource(long sourceId)
    {
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlCommand sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.CommandText = Schema.LoadEventSourceCommand(SourceTId.ToString());
        sqlCommand.Parameters.AddWithValue("@sourceId", sourceId);
        var events = await LoadEvents(() => sqlCommand);
        return events;
    }
    public override async Task<long> LoadMaxSequence()
    {
        await using SqlConnection sqlConnection = new(conn);
        await sqlConnection.OpenAsync();
        await using SqlCommand sqlCommand = new(Schema.GetMaxSequenceId, sqlConnection);
        await using SqlDataReader reader = await sqlCommand.ExecuteReaderAsync();
        await reader.ReadAsync();
        long sequence = reader.HasRows ? (long)reader.GetValue(0) : 0;
        return sequence;
    }
}
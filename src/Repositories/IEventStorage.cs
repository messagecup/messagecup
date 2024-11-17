using EventStorage.Events;
using EventStorage.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace EventStorage.Repositories;

public interface IEventStorage<T>
{
    Task InitSource();
    Task<T> CreateOrRestore(string? sourceId = null);
    Task Commit(T t);
    Task<M?> Project<M>(string sourceId) where M : class;
    internal Task<Checkpoint> LoadCheckpoint();
    internal Task SaveCheckpoint(Checkpoint checkpoint);
    internal Task<IEnumerable<EventEnvelop>> LoadEventsPastCheckpoint(Checkpoint c);
    Task RestoreProjections(EventSourceEnvelop source, IServiceScopeFactory scope);
}
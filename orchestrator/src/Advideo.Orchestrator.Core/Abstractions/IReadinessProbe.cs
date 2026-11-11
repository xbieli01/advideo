namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>Dependency check for <c>/readyz</c> (object store, database, cache …).</summary>
public interface IReadinessProbe
{
    string Name { get; }
    Task CheckAsync(CancellationToken ct);
}

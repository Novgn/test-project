namespace ChatAgent.Application.Tools;

public interface IToolHandler<TIn, TOut>
{
    string Name { get; }
    string Description { get; }
    Task<TOut> HandleAsync(TIn input, CancellationToken cancellationToken = default);
}
namespace PlcMcp.Services.Contracts.Messaging;
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

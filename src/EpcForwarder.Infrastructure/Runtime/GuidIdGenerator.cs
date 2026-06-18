using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Runtime;

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}

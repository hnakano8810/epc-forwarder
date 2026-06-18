using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Runtime;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

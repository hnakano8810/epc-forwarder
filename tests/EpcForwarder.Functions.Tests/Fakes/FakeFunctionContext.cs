using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Tests.Fakes;

/// <summary>テスト専用: FunctionContext の最小実装。Items のみ使用可能。</summary>
internal sealed class FakeFunctionContext : FunctionContext
{
    public override string InvocationId => string.Empty;
    public override string FunctionId => string.Empty;
    public override TraceContext TraceContext => null!;
    public override BindingContext BindingContext => null!;
    public override RetryContext RetryContext => null!;
    public override IServiceProvider InstanceServices { get; set; } = null!;
    public override FunctionDefinition FunctionDefinition => null!;
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features => null!;
}

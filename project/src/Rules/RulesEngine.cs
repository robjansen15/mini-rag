using System;using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pack.Rules
{
public interface IRuleContext
{
IDictionary<string, object> Items { get; }
}

public sealed class RuleContext : IRuleContext
{
public IDictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
public T Get<T>(string key) => Items.TryGetValue(key, out var v) ? (T)v : default;
public void Set<T>(string key, T value) => Items[key] = value;
}

public interface IRule
{
string Id { get; }
int Priority { get; }
IReadOnlyList<string> DependsOn { get; }
bool Applies(IRuleContext ctx);
Task ExecuteAsync(IRuleContext ctx, CancellationToken ct);
}

public abstract class RuleBase : IRule
{
public abstract string Id { get; }
public virtual int Priority => 0;
public virtual IReadOnlyList<string> DependsOn => Array.Empty<string>();
public abstract bool Applies(IRuleContext ctx);
public abstract Task ExecuteAsync(IRuleContext ctx, CancellationToken ct);
}

public sealed class RuleEngine
{
readonly Dictionary<string, IRule> _rules = new Dictionary<string, IRule>(StringComparer.OrdinalIgnoreCase);

public RuleEngine Register(IRule rule)
{
_rules[rule.Id] = rule;
return this;
}

public IReadOnlyList<IRule> Plan(IRuleContext ctx)
{
var applicable = _rules.Values.Where(r => r.Applies(ctx)).ToList();
var graph = BuildGraph(applicable);
var sorted = TopoSort(graph);
return sorted.OrderByDescending(r => r.Priority).ToList();
}

public async Task ExecuteAsync(IRuleContext ctx, CancellationToken ct = default)
{
var plan = Plan(ctx);
foreach (var r in plan)
{
await r.ExecuteAsync(ctx, ct).ConfigureAwait(false);
}
}

Dictionary<string, Node> BuildGraph(List<IRule> rules)
{
var nodes = rules.ToDictionary(r => r.Id, r => new Node(r), StringComparer.OrdinalIgnoreCase);
foreach (var n in nodes.Values)
{
foreach (var dep in n.Rule.DependsOn)
{
if (!nodes.TryGetValue(dep, out var d)) continue;
n.In.Add(d);
d.Out.Add(n);
}
}
return nodes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

}
using System;
{
return (T)JsonSerializer.Deserialize(json, typeof(T), _json);
}
}

public abstract class AggregateBase : IAggregate
{
readonly Queue<IEvent> _queue = new Queue<IEvent>();
public string Id { get; protected set; }
public int Version { get; protected set; }

public IEnumerable<IEvent> DequeueUncommitted()
{
while (_queue.Count > 0) yield return _queue.Dequeue();
}

protected void Emit(IEvent e)
{
When(e);
_queue.Enqueue(e);
}

public void Load(IReadOnlyList<IEvent> events, int version)
{
foreach (var e in events) When(e);
Version = version;
}

protected abstract void When(IEvent e);
}

public sealed class MoneyDeposited : IEvent
{
public string AccountId { get; set; }
public decimal Amount { get; set; }
}

public sealed class MoneyWithdrawn : IEvent
{
public string AccountId { get; set; }
public decimal Amount { get; set; }
}

public sealed class Account : AggregateBase
{
public decimal Balance { get; private set; }
public Account(string id) { Id = id; }
public void Deposit(decimal amount)
{
if (amount <= 0) throw new ArgumentOutOfRangeException();
Emit(new MoneyDeposited { AccountId = Id, Amount = amount });
}
public void Withdraw(decimal amount)
{
if (amount <= 0) throw new ArgumentOutOfRangeException();
if (Balance < amount) throw new InvalidOperationException();
Emit(new MoneyWithdrawn { AccountId = Id, Amount = amount });
}
protected override void When(IEvent e)
{
switch (e)
{
case MoneyDeposited d:
Balance += d.Amount;
break;
case MoneyWithdrawn w:
Balance -= w.Amount;
break;
}
}
}
}
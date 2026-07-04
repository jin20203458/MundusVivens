using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Services;

public interface IWorldContextService
{
    WorldContext GetContext();
    void UpdateEvents(string newEvents);
    void UpdateEra(string newEra);
}

public class WorldContextService : IWorldContextService
{
    private readonly WorldContext _context = new();

    public WorldContext GetContext() => _context;

    public void UpdateEvents(string newEvents)
    {
        _context.ActiveGlobalEvents = newEvents;
    }

    public void UpdateEra(string newEra)
    {
        _context.CurrentEra = newEra;
    }
}

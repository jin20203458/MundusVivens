using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Services;

public interface IWorldContextService
{
    WorldContext GetContext();
}

public class WorldContextService : IWorldContextService
{
    private readonly WorldContext _context = new();

    public WorldContext GetContext() => _context;
}

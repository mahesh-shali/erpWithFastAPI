using Erp.Api.Domain;

namespace Erp.Api.Contracts;

public static class MenuMapping
{
    /// <summary>Requires Page.Group.Module to be loaded (see ErpDbContext.MenuPages()).</summary>
    public static MenuPosition Menu(this Page p) => new(
        p.Group?.Module?.Key, p.Group?.Module?.Name, p.Group?.Module?.Icon, p.Group?.Module?.SortOrder ?? 0,
        p.Group?.Key, p.Group?.Name, p.Group?.SortOrder ?? 0, p.SortOrder);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductCatalog.Web.Models;
using ProductCatalog.Web.Models.Data;
using ProductCatalog.Web.Models.ViewModels;
using ProductCatalog.Web.Security;

namespace ProductCatalog.Web.Controllers;

// Everyone who is signed in can list and search. Only Admin can create, edit and delete.
[Authorize]
public class ProductGroupsController : Controller
{
    private readonly AppDbContext _db;

    public ProductGroupsController(AppDbContext db)
    {
        _db = db;
    }

    // ---------- LIST + SEARCH ----------
    // GET: /ProductGroups?search=lap
    public async Task<IActionResult> Index(string? search)
    {
        var query = _db.ProductGroups.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE = case-insensitive LIKE in PostgreSQL. The value is sent as a parameter (no SQL injection).
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(g =>
                EF.Functions.ILike(g.GroupName, pattern) ||
                (g.Description != null && EF.Functions.ILike(g.Description, pattern)));
        }

        var items = await query
            .OrderBy(g => g.GroupName)
            .Select(g => new ProductGroupListItem
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Description = g.Description,
                ProductCount = g.Products.Count(p => !p.IsDeleted),
                UpdatedAt = g.UpdatedAt
            })
            .ToListAsync();

        return View(new ProductGroupIndexViewModel { Search = search, Items = items });
    }

    // ---------- CREATE ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult Create()
    {
        return View(new ProductGroupFormViewModel());
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Create(ProductGroupFormViewModel model)
    {
        var name = model.GroupName?.Trim() ?? string.Empty;

        if (name.Length > 0 && await NameExistsAsync(name, null))
        {
            ModelState.AddModelError(nameof(model.GroupName), "A group with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var group = new ProductGroup
        {
            GroupName = name,
            Description = CleanText(model.Description)
            // CreatedAt / UpdatedAt are filled by the database (DEFAULT now())
        };

        _db.ProductGroups.Add(group);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two admins saved the same name at the same moment: the UNIQUE constraint caught it
            ModelState.AddModelError(nameof(model.GroupName), "A group with this name already exists.");
            return View(model);
        }

        TempData["Success"] = $"Group \"{group.GroupName}\" was created.";
        return RedirectToAction(nameof(Index));
    }

    // ---------- EDIT ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Edit(int id)
    {
        var group = await _db.ProductGroups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == id);
        if (group == null)
        {
            return NotFound();
        }

        return View(new ProductGroupFormViewModel
        {
            GroupId = group.GroupId,
            GroupName = group.GroupName,
            Description = group.Description
        });
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Edit(int id, ProductGroupFormViewModel model)
    {
        if (id != model.GroupId)
        {
            return BadRequest();
        }

        var group = await _db.ProductGroups.FirstOrDefaultAsync(g => g.GroupId == id);
        if (group == null)
        {
            return NotFound();
        }

        var name = model.GroupName?.Trim() ?? string.Empty;

        if (name.Length > 0 && await NameExistsAsync(name, id))
        {
            ModelState.AddModelError(nameof(model.GroupName), "A group with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        group.GroupName = name;
        group.Description = CleanText(model.Description);
        // updated_at is set by the database trigger trg_product_group_updated_at

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            ModelState.AddModelError(nameof(model.GroupName), "A group with this name already exists.");
            return View(model);
        }

        TempData["Success"] = $"Group \"{group.GroupName}\" was updated.";
        return RedirectToAction(nameof(Index));
    }

    // ---------- DELETE ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Delete(int id)
    {
        var group = await _db.ProductGroups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == id);
        if (group == null)
        {
            return NotFound();
        }

        return View(new ProductGroupDeleteViewModel
        {
            GroupId = group.GroupId,
            GroupName = group.GroupName,
            Description = group.Description,
            ActiveProductCount = await _db.Products.CountAsync(p => p.GroupId == id && !p.IsDeleted),
            TotalProductCount = await _db.Products.CountAsync(p => p.GroupId == id)
        });
    }

    [HttpPost, ActionName("Delete")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var group = await _db.ProductGroups.FirstOrDefaultAsync(g => g.GroupId == id);
        if (group == null)
        {
            return NotFound();
        }

        // Business rule: a group that still has products (even soft-deleted ones) cannot be deleted.
        // The database foreign key (ON DELETE RESTRICT) enforces the same rule as a safety net.
        var hasProducts = await _db.Products.AnyAsync(p => p.GroupId == id);
        if (hasProducts)
        {
            TempData["Error"] = $"Group \"{group.GroupName}\" still has products and cannot be deleted.";
            return RedirectToAction(nameof(Index));
        }

        _db.ProductGroups.Remove(group);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            TempData["Error"] = $"Group \"{group.GroupName}\" is still in use and cannot be deleted.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = $"Group \"{group.GroupName}\" was deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ---------- helpers ----------

    // Case-insensitive duplicate check (excludeId = the group being edited, so it does not clash with itself)
    private Task<bool> NameExistsAsync(string name, int? excludeId)
    {
        var lower = name.ToLower();
        return _db.ProductGroups.AnyAsync(g =>
            g.GroupName.ToLower() == lower && (excludeId == null || g.GroupId != excludeId));
    }

    private static string? CleanText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // Makes %, _ and \ in the user's search text behave as normal characters inside ILIKE
    private static string EscapeLike(string input)
    {
        return input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
